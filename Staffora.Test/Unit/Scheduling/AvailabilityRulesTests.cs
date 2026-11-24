using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;

namespace Staffora.Test.Unit.Scheduling;

public class AvailabilityRulesTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static readonly Guid Staff = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Pattern = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private static readonly DateOnly SpringSunday = new(2026, 3, 29);

    private static readonly DateOnly AutumnSunday = new(2026, 10, 25);

    [Theory]
    [InlineData(1, 30, "2026-03-29T00:30:00Z")]
    [InlineData(2, 0, "2026-03-29T01:00:00Z")]
    [InlineData(2, 30, "2026-03-29T01:00:00Z")]
    [InlineData(2, 59, "2026-03-29T01:00:00Z")]
    [InlineData(3, 0, "2026-03-29T01:00:00Z")]
    [InlineData(3, 30, "2026-03-29T01:30:00Z")]
    public void A_time_the_spring_change_skips_is_the_instant_the_clock_jumps_to(int hour, int minute, string expected)
    {
        Assert.Equal(Utc(expected), WallClock.ToUtc(SpringSunday, new TimeOnly(hour, minute), Stockholm));
    }

    [Theory]
    [InlineData(1, 30, "2026-10-24T23:30:00Z")]
    [InlineData(2, 0, "2026-10-25T00:00:00Z")]
    [InlineData(2, 30, "2026-10-25T00:30:00Z")]
    [InlineData(2, 59, "2026-10-25T00:59:00Z")]
    [InlineData(3, 0, "2026-10-25T02:00:00Z")]
    public void A_time_the_autumn_change_repeats_is_its_first_pass(int hour, int minute, string expected)
    {
        Assert.Equal(Utc(expected), WallClock.ToUtc(AutumnSunday, new TimeOnly(hour, minute), Stockholm));
    }

    [Fact]
    public void The_conversion_through_the_tenant_clock_is_the_wall_clocks()
    {
        Assert.Equal(WallClock.ToUtc(SpringSunday, new TimeOnly(2, 30), Stockholm), Staffora.Services.Settings.TenantClock.ToUtc(SpringSunday, new TimeOnly(2, 30), Stockholm));
        Assert.Equal(DateTimeKind.Utc, WallClock.ToUtc(AutumnSunday, new TimeOnly(2, 30), Stockholm).Kind);
        Assert.Throws<ArgumentException>(() => WallClock.LocalTimeOf(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Local), Stockholm));
    }

    [Fact]
    public void Across_the_spring_change_the_days_around_it_keep_their_hours_in_their_own_offset()
    {
        var working = WeekOf(Weekly(Day(5, 8, 0, 16, 0), Day(0, 8, 0, 16, 0)), new DateOnly(2026, 3, 28), new DateOnly(2026, 3, 30), Stockholm);

        Assert.Equal(
            [(new DateOnly(2026, 3, 28), Utc("2026-03-28T07:00:00Z"), Utc("2026-03-28T15:00:00Z")), (new DateOnly(2026, 3, 30), Utc("2026-03-30T06:00:00Z"), Utc("2026-03-30T14:00:00Z"))],
            working.Select(segment => (segment.Date, segment.StartUtc, segment.EndUtc)));
        Assert.All(working, segment => Assert.Equal((new TimeOnly(8, 0), new TimeOnly(16, 0), TimeSpan.FromHours(8)), (segment.LocalStart, segment.LocalEnd, segment.Duration)));
    }

    [Fact]
    public void A_segment_across_the_skipped_hour_lasts_an_hour_less_than_its_wall_clock_says()
    {
        var segment = Assert.Single(WeekOf(Weekly(Day(6, 1, 0, 4, 0)), SpringSunday, SpringSunday, Stockholm));

        Assert.Equal((Utc("2026-03-29T00:00:00Z"), Utc("2026-03-29T02:00:00Z")), (segment.StartUtc, segment.EndUtc));
        Assert.Equal((new TimeOnly(1, 0), new TimeOnly(4, 0), TimeSpan.FromHours(2)), (segment.LocalStart, segment.LocalEnd, segment.Duration));
    }

    [Fact]
    public void A_segment_that_starts_in_the_skipped_hour_starts_as_the_clock_jumps()
    {
        var segment = Assert.Single(WeekOf(Weekly(Day(6, 2, 30, 5, 0)), SpringSunday, SpringSunday, Stockholm));

        Assert.Equal((Utc("2026-03-29T01:00:00Z"), Utc("2026-03-29T03:00:00Z")), (segment.StartUtc, segment.EndUtc));
        Assert.Equal((new TimeOnly(3, 0), new TimeOnly(5, 0)), (segment.LocalStart, segment.LocalEnd));
    }

    [Fact]
    public void A_segment_wholly_inside_the_skipped_hour_is_no_working_time()
    {
        Assert.Empty(WeekOf(Weekly(Day(6, 2, 15, 2, 45)), SpringSunday, SpringSunday, Stockholm));

        var around = WeekOf(Weekly(Day(6, 2, 15, 2, 45)), SpringSunday.AddDays(-7), SpringSunday.AddDays(7), Stockholm);
        Assert.Equal([SpringSunday.AddDays(-7), SpringSunday.AddDays(7)], around.Select(segment => segment.Date));
        Assert.All(around, segment => Assert.Equal(TimeSpan.FromMinutes(30), segment.Duration));
    }

    [Fact]
    public void A_segment_inside_the_repeated_hour_is_its_first_pass()
    {
        var segment = Assert.Single(WeekOf(Weekly(Day(6, 2, 15, 2, 45)), AutumnSunday, AutumnSunday, Stockholm));

        Assert.Equal((Utc("2026-10-25T00:15:00Z"), Utc("2026-10-25T00:45:00Z")), (segment.StartUtc, segment.EndUtc));
        Assert.Equal((new TimeOnly(2, 15), new TimeOnly(2, 45), TimeSpan.FromMinutes(30)), (segment.LocalStart, segment.LocalEnd, segment.Duration));
    }

    [Fact]
    public void A_segment_across_the_repeated_hour_lasts_an_hour_more_than_its_wall_clock_says()
    {
        var segment = Assert.Single(WeekOf(Weekly(Day(6, 1, 0, 4, 0)), AutumnSunday, AutumnSunday, Stockholm));

        Assert.Equal((Utc("2026-10-24T23:00:00Z"), Utc("2026-10-25T03:00:00Z")), (segment.StartUtc, segment.EndUtc));
        Assert.Equal((new TimeOnly(1, 0), new TimeOnly(4, 0), TimeSpan.FromHours(4)), (segment.LocalStart, segment.LocalEnd, segment.Duration));
    }

    [Fact]
    public void Across_the_autumn_change_the_days_around_it_keep_their_hours_in_their_own_offset()
    {
        var working = WeekOf(Weekly(Day(5, 8, 0, 16, 0), Day(0, 8, 0, 16, 0)), new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 26), Stockholm);

        Assert.Equal(
            [(Utc("2026-10-24T06:00:00Z"), Utc("2026-10-24T14:00:00Z")), (Utc("2026-10-26T07:00:00Z"), Utc("2026-10-26T15:00:00Z"))],
            working.Select(segment => (segment.StartUtc, segment.EndUtc)));
    }

    [Fact]
    public void A_staff_members_week_with_a_clock_change_counts_the_hour_in_real_time()
    {
        var pattern = Weekly([.. Enumerable.Range(0, 7).Select(day => Day(day, 1, 0, 4, 0))]);
        var spring = StaffSchedule.ByWeek(AvailabilityOf(pattern, new DateOnly(2026, 3, 23), new DateOnly(2026, 3, 29), Stockholm), new DateOnly(2026, 3, 23), 1, Stockholm.Id);
        var autumn = StaffSchedule.ByWeek(AvailabilityOf(pattern, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), Stockholm), new DateOnly(2026, 10, 19), 1, Stockholm.Id);

        Assert.Equal(TimeSpan.FromHours(20), Assert.Single(spring.Weeks).Worked);
        Assert.Equal(TimeSpan.FromHours(22), Assert.Single(autumn.Weeks).Worked);
        Assert.Equal("01:00–04:00", spring.Weeks[0].Days[6].HoursLabel);
    }

    [Fact]
    public void A_two_week_cycle_runs_on_across_a_years_end_from_its_Monday()
    {
        var pattern = new PatternShape(Pattern, 2, [new PatternSegment(0, new TimeOnly(8, 0), new TimeOnly(12, 0)), new PatternSegment(7, new TimeOnly(13, 0), new TimeOnly(17, 0))]);
        var assignment = new ScheduledAssignment(Guid.NewGuid(), Staff, Pattern, new DateOnly(2026, 12, 1), null, new DateOnly(2026, 12, 21));

        var working = Compute([assignment], pattern, new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 18), Stockholm);

        Assert.Equal(
            [
                (new DateOnly(2026, 12, 28), Utc("2026-12-28T12:00:00Z")),
                (new DateOnly(2027, 1, 4), Utc("2027-01-04T07:00:00Z")),
                (new DateOnly(2027, 1, 11), Utc("2027-01-11T12:00:00Z")),
                (new DateOnly(2027, 1, 18), Utc("2027-01-18T07:00:00Z")),
            ],
            working.Select(segment => (segment.Date, segment.StartUtc)));
    }

    [Fact]
    public void In_New_York_the_spring_change_of_its_own_date_moves_the_instants_by_its_offset()
    {
        var working = WeekOf(Weekly(Day(4, 8, 0, 16, 0), Day(0, 8, 0, 16, 0), Day(6, 2, 30, 3, 30)), new DateOnly(2026, 3, 6), new DateOnly(2026, 3, 9), NewYork);

        Assert.Equal(
            [
                (new DateOnly(2026, 3, 6), Utc("2026-03-06T13:00:00Z"), Utc("2026-03-06T21:00:00Z")),
                (new DateOnly(2026, 3, 8), Utc("2026-03-08T07:00:00Z"), Utc("2026-03-08T07:30:00Z")),
                (new DateOnly(2026, 3, 9), Utc("2026-03-09T12:00:00Z"), Utc("2026-03-09T20:00:00Z")),
            ],
            working.Select(segment => (segment.Date, segment.StartUtc, segment.EndUtc)));
    }

    [Fact]
    public void A_zone_without_clock_changes_and_a_half_hour_offset_converts_every_day_alike()
    {
        var working = WeekOf(Weekly(Day(6, 9, 0, 17, 30)), new DateOnly(2026, 3, 22), new DateOnly(2026, 11, 1), Kolkata);

        Assert.Equal(33, working.Count);
        Assert.All(working, segment => Assert.Equal((new TimeSpan(3, 30, 0), new TimeSpan(12, 0, 0), TimeSpan.FromHours(8.5)), (segment.StartUtc.TimeOfDay, segment.EndUtc.TimeOfDay, segment.Duration)));
    }

    [Fact]
    public void Each_day_takes_the_assignment_that_applies_on_it_and_an_ended_one_gives_way_the_day_after()
    {
        var early = new PatternShape(Pattern, 1, [new PatternSegment(0, new TimeOnly(8, 0), new TimeOnly(12, 0))]);
        var lateId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
        var late = new PatternShape(lateId, 1, [new PatternSegment(0, new TimeOnly(13, 0), new TimeOnly(17, 0))]);
        var first = new ScheduledAssignment(Guid.NewGuid(), Staff, Pattern, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 2));
        var second = new ScheduledAssignment(Guid.NewGuid(), Staff, lateId, new DateOnly(2026, 11, 16), null, new DateOnly(2026, 11, 16));
        var other = new ScheduledAssignment(Guid.NewGuid(), Guid.NewGuid(), Pattern, new DateOnly(2026, 1, 5), null, new DateOnly(2026, 1, 5));

        var answer = AvailabilityRules.Compute([Staff], [first, second, other], new Dictionary<Guid, PatternShape> { [Pattern] = early, [lateId] = late }, new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 29), Stockholm);

        var working = Assert.Single(answer).Value.Working;
        Assert.Equal(
            [(new DateOnly(2026, 11, 2), new TimeOnly(8, 0), first.AssignmentId), (new DateOnly(2026, 11, 9), new TimeOnly(8, 0), first.AssignmentId), (new DateOnly(2026, 11, 16), new TimeOnly(13, 0), second.AssignmentId), (new DateOnly(2026, 11, 23), new TimeOnly(13, 0), second.AssignmentId)],
            working.Select(segment => (segment.Date, segment.LocalStart, segment.AssignmentId)));
        Assert.Empty(answer[Staff].Blocks);
        Assert.Empty(answer[Staff].Warnings);
    }

    [Fact]
    public void Rows_written_past_the_rules_that_share_a_date_give_it_to_the_one_that_starts_latest()
    {
        var older = new ScheduledAssignment(Guid.NewGuid(), Staff, Pattern, new DateOnly(2026, 11, 2), null, new DateOnly(2026, 11, 2));
        var newer = new ScheduledAssignment(Guid.NewGuid(), Staff, Pattern, new DateOnly(2026, 11, 9), null, new DateOnly(2026, 11, 9));

        var working = Compute([older, newer], Weekly(Day(0, 8, 0, 12, 0)), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 16), Stockholm);

        Assert.Equal([older.AssignmentId, newer.AssignmentId, newer.AssignmentId], working.Select(segment => segment.AssignmentId));
    }

    [Fact]
    public void A_staff_member_without_an_assignment_and_an_empty_range_have_no_working_time()
    {
        var assignment = new ScheduledAssignment(Guid.NewGuid(), Staff, Pattern, new DateOnly(2026, 11, 2), null, new DateOnly(2026, 11, 2));
        var nobody = Guid.NewGuid();

        var answer = AvailabilityRules.Compute([Staff, nobody], [assignment], new Dictionary<Guid, PatternShape> { [Pattern] = Weekly(Day(0, 8, 0, 12, 0)) }, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 8), Stockholm);

        Assert.Equal(new[] { Staff, nobody }.Order(), answer.Keys.Order());
        Assert.All(answer.Values, staff => Assert.Empty(staff.Working));
        Assert.Empty(AvailabilityRules.Compute([nobody], [assignment], new Dictionary<Guid, PatternShape>(), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), Stockholm)[nobody].Working);
    }

    [Fact]
    public void An_assignment_whose_pattern_was_not_read_is_an_error_not_a_free_staff_member()
    {
        var assignment = new ScheduledAssignment(Guid.NewGuid(), Staff, Pattern, new DateOnly(2026, 11, 2), null, new DateOnly(2026, 11, 2));

        Assert.Throws<InvalidOperationException>(() => AvailabilityRules.Compute([Staff], [assignment], new Dictionary<Guid, PatternShape>(), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), Stockholm));
    }

    [Fact]
    public void Working_time_covers_a_stretch_only_when_it_lies_wholly_in_one_run_of_segments()
    {
        var staff = AvailabilityOf(Weekly(Day(0, 8, 0, 12, 0), Day(0, 12, 0, 16, 0), Day(1, 8, 0, 12, 0), Day(1, 13, 0, 16, 0)), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 10), Stockholm);

        Assert.True(staff.IsWithinWorkingTime(Utc("2026-11-09T09:00:00Z"), Utc("2026-11-09T13:00:00Z")));
        Assert.True(staff.IsWithinWorkingTime(Utc("2026-11-10T12:00:00Z"), Utc("2026-11-10T15:00:00Z")));
        Assert.False(staff.IsWithinWorkingTime(Utc("2026-11-10T10:00:00Z"), Utc("2026-11-10T13:00:00Z")));
        Assert.False(staff.IsWithinWorkingTime(Utc("2026-11-09T06:00:00Z"), Utc("2026-11-09T08:00:00Z")));
        Assert.False(staff.IsWithinWorkingTime(Utc("2026-11-09T09:00:00Z"), Utc("2026-11-09T09:00:00Z")));
        Assert.Equal(2, staff.On(new DateOnly(2026, 11, 10)).Count());

        var shuffled = staff with { Working = [.. staff.Working.Reverse()] };
        var ordered = shuffled.OrderedWorking();
        foreach (var (start, end, within) in new[] { ("2026-11-09T09:00:00Z", "2026-11-09T13:00:00Z", true), ("2026-11-10T12:00:00Z", "2026-11-10T15:00:00Z", true), ("2026-11-10T10:00:00Z", "2026-11-10T13:00:00Z", false), ("2026-11-09T06:00:00Z", "2026-11-09T08:00:00Z", false), ("2026-11-10T11:30:00Z", "2026-11-10T12:30:00Z", false) })
        {
            Assert.Equal(within, shuffled.IsWithinWorkingTime(Utc(start), Utc(end)));
            Assert.Equal(within, StaffAvailability.IsWithin(ordered, Utc(start), Utc(end)));
        }
    }

    [Fact]
    public void A_schedule_by_week_has_every_day_of_its_weeks_and_starts_on_a_Monday()
    {
        var schedule = StaffSchedule.ByWeek(AvailabilityOf(Weekly(Day(0, 8, 0, 16, 0), Day(4, 8, 0, 12, 0)), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 22), Stockholm), new DateOnly(2026, 11, 9), 2, Stockholm.Id);

        Assert.Equal([new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 16)], schedule.Weeks.Select(week => week.Monday));
        Assert.All(schedule.Weeks, week => Assert.Equal(7, week.Days.Count));
        Assert.Equal(["08:00–16:00", "", "", "", "08:00–12:00", "", ""], schedule.Weeks[0].Days.Select(day => day.HoursLabel));
        Assert.Equal("12 h", schedule.Weeks[1].WorkedLabel);
        Assert.Equal(new DateOnly(2026, 11, 22), StaffSchedule.LastDayOf(new DateOnly(2026, 11, 9), 2));
        var tuesday = new DateOnly(2026, 11, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => StaffSchedule.ByWeek(AvailabilityOf(Weekly(), tuesday, tuesday, Stockholm), tuesday, 1, Stockholm.Id));
    }

    [Fact]
    public void Assigned_and_accepted_occurrences_block_an_open_request_warns_and_each_occurrence_keeps_its_wall_clock_across_the_spring_change()
    {
        Guid assigned = Guid.NewGuid(), accepted = Guid.NewGuid(), requested = Guid.NewGuid(), declined = Guid.NewGuid();
        Guid other = Guid.Parse("00000000-0000-0000-0000-0000000000a2"), third = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
        OccurrenceTimes[] mondays = [new(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))];
        ScheduledBooking[] bookings =
        [
            new(assigned, Staff, SlotAssignmentState.Assigned, new DateOnly(2026, 3, 23), new DateOnly(2026, 4, 3), mondays),
            new(accepted, other, SlotAssignmentState.Accepted, new DateOnly(2026, 3, 23), new DateOnly(2026, 4, 3), mondays),
            new(requested, third, SlotAssignmentState.Requested, new DateOnly(2026, 3, 23), new DateOnly(2026, 4, 3), mondays),
            new(declined, third, SlotAssignmentState.Declined, new DateOnly(2026, 3, 23), new DateOnly(2026, 4, 3), mondays),
        ];
        ScheduledStaff[] staffMembers = [new(Staff, true), new(other, true), new(third, true)];

        var answer = AvailabilityRules.Compute(staffMembers, [], new Dictionary<Guid, PatternShape>(), [], bookings, new DateOnly(2026, 3, 20), new DateOnly(2026, 4, 10), Stockholm);

        (DateTime, DateTime)[] occurrences = [(Utc("2026-03-23T07:00:00Z"), Utc("2026-03-23T09:00:00Z")), (Utc("2026-03-30T06:00:00Z"), Utc("2026-03-30T08:00:00Z"))];
        Assert.Equal(occurrences, answer[Staff].Blocks.Select(block => (block.StartUtc, block.EndUtc)));
        Assert.All(answer[Staff].Blocks, block => Assert.Equal((AvailabilityRules.AssignedReason, (Guid?)assigned), (block.Reason, block.SourceId)));
        Assert.Equal(occurrences, answer[other].Blocks.Select(block => (block.StartUtc, block.EndUtc)));
        Assert.All(answer[other].Blocks, block => Assert.Equal(AvailabilityRules.AcceptedReason, block.Reason));
        Assert.Empty(answer[third].Blocks);
        Assert.Equal(occurrences, answer[third].Warnings.Select(warning => (warning.StartUtc, warning.EndUtc)));
        Assert.All(answer[third].Warnings, warning => Assert.Equal((AvailabilityRules.RequestedReason, (Guid?)requested), (warning.Reason, warning.SourceId)));
        Assert.Empty(answer[Staff].Warnings);
    }

    [Fact]
    public void A_booking_gives_only_the_occurrences_of_its_period_inside_the_range_and_nothing_for_a_staff_member_out_of_service()
    {
        OccurrenceTimes[] week = [new(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0)), new(Weekday.Friday, new TimeOnly(13, 0), new TimeOnly(14, 0))];
        var booking = new ScheduledBooking(Guid.NewGuid(), Staff, SlotAssignmentState.Assigned, new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 23), week);

        var inService = AvailabilityRules.Compute([new ScheduledStaff(Staff, true)], [], new Dictionary<Guid, PatternShape>(), [], [booking], new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 19), Stockholm)[Staff];
        Assert.Equal([new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 16)], inService.Blocks.Select(block => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(block.StartUtc, Stockholm))));

        var outOfService = AvailabilityRules.Compute([new ScheduledStaff(Staff, false)], [], new Dictionary<Guid, PatternShape>(), [], [booking], new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 19), Stockholm)[Staff];
        Assert.Equal([AbsenceRules.NotInServiceReason], outOfService.Blocks.Select(block => block.Reason));
    }

    private static DateTime Utc(string instant) => DateTime.SpecifyKind(DateTime.Parse(instant, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);

    private static PatternSegment Day(int dayIndex, int startHour, int startMinute, int endHour, int endMinute) =>
        new(dayIndex, new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute));

    private static PatternShape Weekly(params PatternSegment[] segments) => new(Pattern, 1, segments);

    private static StaffAvailability AvailabilityOf(PatternShape pattern, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var assignment = new ScheduledAssignment(Guid.NewGuid(), Staff, pattern.WorkPatternId, new DateOnly(2026, 1, 5), null, new DateOnly(2026, 1, 5));
        return AvailabilityRules.Compute([Staff], [assignment], new Dictionary<Guid, PatternShape> { [pattern.WorkPatternId] = pattern }, from, to, zone)[Staff];
    }

    private static IReadOnlyList<WorkingTime> WeekOf(PatternShape pattern, DateOnly from, DateOnly to, TimeZoneInfo zone) => AvailabilityOf(pattern, from, to, zone).Working;

    private static IReadOnlyList<WorkingTime> Compute(IEnumerable<ScheduledAssignment> assignments, PatternShape pattern, DateOnly from, DateOnly to, TimeZoneInfo zone) =>
        AvailabilityRules.Compute([Staff], assignments, new Dictionary<Guid, PatternShape> { [pattern.WorkPatternId] = pattern }, from, to, zone)[Staff].Working;
}
