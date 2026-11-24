using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;

namespace Staffora.Test.Unit.Scheduling;

public class AbsenceRulesTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    private static readonly Guid Staff = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly DateOnly Monday = new(2026, 11, 9);

    private static readonly DateOnly SpringSunday = new(2026, 3, 29);

    private static readonly DateOnly AutumnSunday = new(2026, 10, 25);

    [Fact]
    public void The_question_states_are_requested_with_a_turn_attached_and_say_whose_turn_it_is()
    {
        Assert.Equal([ResourceAbsenceState.Requested, ResourceAbsenceState.Question, ResourceAbsenceState.StaffQuestion], AbsenceRules.UndecidedStates);
        Assert.Equal([.. AbsenceRules.UndecidedStates, ResourceAbsenceState.Approved], AbsenceRules.ScheduledStates);
        Assert.All(Enum.GetValues<ResourceAbsenceState>(), state => Assert.Equal(AbsenceRules.UndecidedStates.Contains(state), AbsenceRules.IsUndecided(state)));
        Assert.Equal(
            ["waiting for approval", "approved", "rejected", "canceled", "a question to you", "your question to the coordinator"],
            Enum.GetValues<ResourceAbsenceState>().Select(AbsenceRules.OwnStatusText));
    }

    [Theory]
    [InlineData(AbsenceKind.PlannedLeave, ResourceAbsenceState.Requested, false)]
    [InlineData(AbsenceKind.PlannedLeave, ResourceAbsenceState.Approved, true)]
    [InlineData(AbsenceKind.PlannedLeave, ResourceAbsenceState.Rejected, false)]
    [InlineData(AbsenceKind.PlannedLeave, ResourceAbsenceState.Canceled, false)]
    [InlineData(AbsenceKind.SickLeave, ResourceAbsenceState.Requested, true)]
    [InlineData(AbsenceKind.SickLeave, ResourceAbsenceState.Approved, true)]
    [InlineData(AbsenceKind.SickLeave, ResourceAbsenceState.Rejected, false)]
    [InlineData(AbsenceKind.SickLeave, ResourceAbsenceState.Canceled, false)]
    [InlineData(AbsenceKind.PlannedLeave, ResourceAbsenceState.Question, false)]
    [InlineData(AbsenceKind.PlannedLeave, ResourceAbsenceState.StaffQuestion, false)]
    [InlineData(AbsenceKind.SickLeave, ResourceAbsenceState.Question, true)]
    [InlineData(AbsenceKind.SickLeave, ResourceAbsenceState.StaffQuestion, true)]
    public void An_approved_absence_and_a_reported_sick_leave_block_and_nothing_else_does(AbsenceKind kind, ResourceAbsenceState state, bool blocks)
    {
        Assert.Equal(blocks, AbsenceRules.Blocks(kind, state));
        var answer = AvailabilityRules.Compute([new ScheduledStaff(Staff, true)], [], new Dictionary<Guid, PatternShape>(), [Absence(kind, state, Monday, null, Monday, null)], Monday, Monday, Stockholm);
        Assert.Equal(blocks ? 1 : 0, answer[Staff].Blocks.Count);
    }

    [Fact]
    public void A_period_is_refused_for_each_thing_wrong_with_it()
    {
        Assert.Empty(AbsenceRules.Problems(Monday, null, Monday, null));
        Assert.Empty(AbsenceRules.Problems(Monday, new TimeOnly(13, 0), Monday, new TimeOnly(16, 0)));
        Assert.Empty(AbsenceRules.Problems(Monday, new TimeOnly(16, 0), Monday.AddDays(1), new TimeOnly(8, 0)));
        Assert.Equal([AbsenceRules.EndsBeforeStart], AbsenceRules.Problems(Monday, null, Monday.AddDays(-1), null));
        Assert.Equal([AbsenceRules.TimesOutOfOrder], AbsenceRules.Problems(Monday, new TimeOnly(13, 0), Monday, new TimeOnly(13, 0)));
        Assert.Equal([AbsenceRules.EndsAtMidnight], AbsenceRules.Problems(Monday, null, Monday.AddDays(1), TimeOnly.MinValue));
        Assert.Equal([AbsenceRules.OutOfCalendar], AbsenceRules.Problems(Monday, null, DateOnly.MaxValue, null));
        Assert.Equal([AbsenceRules.OutOfCalendar], AbsenceRules.Problems(DateOnly.MinValue, null, DateOnly.MinValue, null));
    }

    [Fact]
    public void A_whole_day_lasts_what_the_wall_clock_gives_it_across_both_clock_changes()
    {
        var (springStart, springEnd) = AbsenceRules.Instants(SpringSunday, null, SpringSunday, null, Stockholm);
        Assert.Equal((Utc("2026-03-28T23:00:00Z"), Utc("2026-03-29T22:00:00Z")), (springStart, springEnd));
        Assert.Equal(TimeSpan.FromHours(23), springEnd - springStart);

        var (autumnStart, autumnEnd) = AbsenceRules.Instants(AutumnSunday, null, AutumnSunday, null, Stockholm);
        Assert.Equal((Utc("2026-10-24T22:00:00Z"), Utc("2026-10-25T23:00:00Z")), (autumnStart, autumnEnd));
        Assert.Equal(TimeSpan.FromHours(25), autumnEnd - autumnStart);
    }

    [Fact]
    public void A_time_the_clock_skips_is_the_instant_it_jumps_to_and_a_repeated_time_its_first_pass()
    {
        Assert.Equal(Utc("2026-03-29T01:00:00Z"), AbsenceRules.Instants(SpringSunday, new TimeOnly(2, 30), SpringSunday, null, Stockholm).StartUtc);

        Assert.Equal(Utc("2026-10-25T00:30:00Z"), AbsenceRules.Instants(AutumnSunday, null, AutumnSunday, new TimeOnly(2, 30), Stockholm).EndUtc);

        var answer = AvailabilityRules.Compute([new ScheduledStaff(Staff, true)], [], new Dictionary<Guid, PatternShape>(), [Absence(AbsenceKind.SickLeave, ResourceAbsenceState.Requested, SpringSunday, new TimeOnly(2, 10), SpringSunday, new TimeOnly(2, 50))], SpringSunday, SpringSunday, Stockholm);
        Assert.Empty(answer[Staff].Blocks);
    }

    [Fact]
    public void Blocks_are_cut_to_the_range_ordered_by_start_and_kept_apart_when_they_overlap()
    {
        var leave = Absence(AbsenceKind.PlannedLeave, ResourceAbsenceState.Approved, Monday.AddDays(-10), null, Monday.AddDays(20), null);
        var afternoon = Absence(AbsenceKind.SickLeave, ResourceAbsenceState.Requested, Monday.AddDays(2), new TimeOnly(13, 0), Monday.AddDays(2), new TimeOnly(16, 0));
        var others = Absence(AbsenceKind.SickLeave, ResourceAbsenceState.Approved, Monday, null, Monday, null, Other);
        var outside = Absence(AbsenceKind.SickLeave, ResourceAbsenceState.Approved, Monday.AddDays(-3), null, Monday.AddDays(-1), null);

        var answer = AvailabilityRules.Compute([new ScheduledStaff(Staff, true)], [], new Dictionary<Guid, PatternShape>(), [afternoon, leave, others, outside], Monday, Monday.AddDays(6), Stockholm);

        Assert.Equal(
            [
                (Utc("2026-11-08T23:00:00Z"), Utc("2026-11-15T23:00:00Z"), "On planned leave", leave.AbsenceId),
                (Utc("2026-11-11T12:00:00Z"), Utc("2026-11-11T15:00:00Z"), "On sick leave (reported)", afternoon.AbsenceId),
            ],
            answer[Staff].Blocks.Select(block => (block.StartUtc, block.EndUtc, block.Reason, block.SourceId!.Value)));
        Assert.False(answer.ContainsKey(Other));
    }

    [Fact]
    public void A_staff_member_out_of_service_has_no_working_time_and_one_block_over_the_whole_range()
    {
        var pattern = new PatternShape(Guid.NewGuid(), 1, [.. Enumerable.Range(0, 5).Select(day => new PatternSegment(day, new TimeOnly(8, 0), new TimeOnly(16, 0)))]);
        var assignment = new ScheduledAssignment(Guid.NewGuid(), Staff, pattern.WorkPatternId, Monday, null, Monday);
        var sick = Absence(AbsenceKind.SickLeave, ResourceAbsenceState.Approved, Monday, null, Monday, null);

        var answer = AvailabilityRules.Compute([new ScheduledStaff(Staff, false)], [assignment], new Dictionary<Guid, PatternShape> { [pattern.WorkPatternId] = pattern }, [sick], Monday, Monday.AddDays(6), Stockholm);

        Assert.Empty(answer[Staff].Working);
        var block = Assert.Single(answer[Staff].Blocks);
        Assert.Equal((Utc("2026-11-08T23:00:00Z"), Utc("2026-11-15T23:00:00Z"), AbsenceRules.NotInServiceReason, (Guid?)null), (block.StartUtc, block.EndUtc, block.Reason, block.SourceId));

        var serving = AvailabilityRules.Compute([new ScheduledStaff(Staff, true)], [assignment], new Dictionary<Guid, PatternShape> { [pattern.WorkPatternId] = pattern }, [sick], Monday, Monday.AddDays(6), Stockholm);
        Assert.Equal(5, serving[Staff].Working.Count);
        Assert.Equal([sick.AbsenceId], serving[Staff].Blocks.Select(item => item.SourceId!.Value));
        var twice = AvailabilityRules.Compute([new ScheduledStaff(Staff, true), new ScheduledStaff(Staff, false)], [assignment], new Dictionary<Guid, PatternShape> { [pattern.WorkPatternId] = pattern }, [], Monday, Monday, Stockholm);
        Assert.Empty(twice[Staff].Working);
    }

    [Fact]
    public void A_period_reads_in_words_the_same_in_every_culture()
    {
        Assert.Equal("on 2026-11-09", AbsenceRules.PeriodText(Monday, null, Monday, null));
        Assert.Equal("on 2026-11-09 from 13:00 until 16:30", AbsenceRules.PeriodText(Monday, new TimeOnly(13, 0), Monday, new TimeOnly(16, 30)));
        Assert.Equal("on 2026-11-09 from 13:00", AbsenceRules.PeriodText(Monday, new TimeOnly(13, 0), Monday, null));
        Assert.Equal("on 2026-11-09 until 10:00", AbsenceRules.PeriodText(Monday, null, Monday, new TimeOnly(10, 0)));
        Assert.Equal("from 2026-11-09 to 2026-11-13", AbsenceRules.PeriodText(Monday, null, Monday.AddDays(4), null));
        Assert.Equal("from 2026-11-09 13:00 to 2026-11-13 10:00", AbsenceRules.PeriodText(Monday, new TimeOnly(13, 0), Monday.AddDays(4), new TimeOnly(10, 0)));
    }

    private static ScheduledAbsence Absence(AbsenceKind kind, ResourceAbsenceState state, DateOnly first, TimeOnly? start, DateOnly last, TimeOnly? end, Guid? staff = null) =>
        new(Guid.NewGuid(), staff ?? Staff, kind, state, first, start, last, end);

    private static DateTime Utc(string instant) => DateTime.SpecifyKind(DateTime.Parse(instant, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
}
