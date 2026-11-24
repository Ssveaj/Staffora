using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Scheduling;

public class ClosedDayRulesTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static readonly Guid North = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid NorthTeam = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid South = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    private static readonly Guid Ada = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bo = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Cy = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    private static readonly Guid Holiday = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid StudyDay = Guid.Parse("00000000-0000-0000-0000-0000000000d2");

    private static readonly DateOnly Friday = new(2027, 3, 12);

    [Fact]
    public void A_closed_day_of_the_whole_organization_blocks_every_staff_member_in_service_from_midnight_to_midnight_in_the_tenants_zone()
    {
        var answer = Compute([Staff(Ada, South), Staff(Bo), Staff(Cy, North) with { InService = false }], [new ScheduledClosedDay(Holiday, "Founders' Day", Friday, Friday, null)], Friday.AddDays(-1), Friday.AddDays(1));

        foreach (var staff in new[] { Ada, Bo })
        {
            var block = Assert.Single(answer[staff].Blocks);
            Assert.Equal((Utc("2027-03-11T23:00:00Z"), Utc("2027-03-12T23:00:00Z"), "Closed for Founders' Day", (Guid?)Holiday), (block.StartUtc, block.EndUtc, block.Reason, block.SourceId));
            Assert.Equal([new ClosedTime(Friday, Holiday, "Founders' Day")], answer[staff].Closed);
            Assert.Equal(["Founders' Day"], answer[staff].ClosedOn(Friday).Select(closed => closed.Name));
            Assert.Empty(answer[staff].ClosedOn(Friday.AddDays(1)));
        }

        Assert.Equal([AbsenceRules.NotInServiceReason], answer[Cy].Blocks.Select(block => block.Reason));
        Assert.Empty(answer[Cy].Closed);
    }

    [Fact]
    public void A_closed_day_of_a_unit_blocks_the_staff_members_of_the_unit_and_of_the_units_below_it_and_nobody_else()
    {
        var answer = Compute(
            [Staff(Ada, NorthTeam), Staff(Bo, South), Staff(Cy, South, North), Staff(Guid.NewGuid())],
            [new ScheduledClosedDay(StudyDay, "Study day", Friday, Friday, new HashSet<Guid> { North, NorthTeam })],
            Friday,
            Friday);

        Assert.Single(answer[Ada].Blocks);
        Assert.Empty(answer[Bo].Blocks);
        Assert.Single(answer[Cy].Blocks);
        Assert.Single(answer.Values, staff => staff.Blocks.Count == 0 && staff.ResourceId != Bo);
    }

    [Fact]
    public void A_closed_period_is_cut_to_the_range_read_listed_day_by_day_and_blocks_a_job_occurrence_on_it_as_an_absence_does()
    {
        var answer = Compute([Staff(Ada)], [new ScheduledClosedDay(Holiday, "Spring break", Friday.AddDays(-10), Friday.AddDays(3), null)], Friday, Friday.AddDays(6));

        var block = Assert.Single(answer[Ada].Blocks);
        Assert.Equal((Utc("2027-03-11T23:00:00Z"), Utc("2027-03-15T23:00:00Z")), (block.StartUtc, block.EndUtc));
        Assert.Equal([Friday, Friday.AddDays(1), Friday.AddDays(2), Friday.AddDays(3)], answer[Ada].Closed.Select(closed => closed.Date));

        var occurrences = JobRules.Occurrences(new JobTimes(JobSchedule.Single, Friday, Friday, new TimeOnly(9, 0), new TimeOnly(11, 0), []), Friday, Friday, Stockholm);
        var verdict = StaffingRules.Judge(answer[Ada], occurrences);
        Assert.True(verdict.IsBlocked);
        Assert.Equal(["Closed for Spring break: 1 occurrence, 2027-03-12"], verdict.BlockGroups.Select(group => group.Text));
    }

    [Theory]
    [InlineData("", 0, 0, "A closed day needs a name.")]
    [InlineData("Holiday", 0, -1, ClosedDayRules.RangeReversed)]
    [InlineData("Holiday", 0, 366, "A closed period lasts at most 366 days.")]
    [InlineData("Holiday", 0, 365, null)]
    [InlineData("Holiday", 0, 0, null)]
    public void A_closed_day_needs_a_name_and_a_period_of_one_day_to_a_year_in_order(string name, int first, int last, string? problem)
    {
        Assert.Equal(problem, ClosedDayRules.Problem(name, Friday.AddDays(first), Friday.AddDays(last)));
    }

    [Fact]
    public void A_name_too_long_and_a_day_outside_the_calendar_are_refused()
    {
        Assert.Equal(ClosedDayRules.TextRule.NameTooLong, ClosedDayRules.Problem(new string('x', 101), Friday, Friday));
        Assert.Equal(ClosedDayRules.OutOfCalendar, ClosedDayRules.Problem("Holiday", DateOnly.MinValue, Friday));
        Assert.Equal(ClosedDayRules.OutOfCalendar, ClosedDayRules.Problem("Holiday", Friday, DateOnly.MaxValue));
    }

    [Fact]
    public void The_calendar_marks_each_day_with_the_whole_organizations_closed_days_first_and_names_a_units()
    {
        var marks = ClosedDayRules.MarkDays(
            [new ClosedDayFact(StudyDay, "Study day", Friday, Friday.AddDays(1), "North"), new ClosedDayFact(Holiday, "Founders' Day", Friday, Friday, null), new ClosedDayFact(Guid.NewGuid(), "Long ago", Friday.AddDays(-40), Friday.AddDays(-30), null)],
            Friday.AddDays(-1),
            Friday.AddDays(7));

        Assert.Equal([Friday, Friday.AddDays(1)], marks.Keys.Order());
        Assert.Equal("Closed for Founders' Day, Closed for Study day in North", ClosedDayRules.MarksText(marks[Friday]));
        Assert.Equal("Closed for Study day in North", ClosedDayRules.MarksText(marks[Friday.AddDays(1)]));
        Assert.Equal("Fri 12 Mar 2027 to Sat 13 Mar 2027", ClosedDayRules.PeriodText(Friday, Friday.AddDays(1)));
        Assert.Equal("Fri 12 Mar 2027", ClosedDayRules.PeriodText(Friday, Friday));
    }

    private static ScheduledStaff Staff(Guid resourceId, params Guid[] units) => new(resourceId, InService: true) { Units = units.ToHashSet() };

    private static IReadOnlyDictionary<Guid, StaffAvailability> Compute(IEnumerable<ScheduledStaff> staff, IEnumerable<ScheduledClosedDay> closedDays, DateOnly from, DateOnly to) =>
        AvailabilityRules.Compute(staff, [], new Dictionary<Guid, PatternShape>(), [], [], closedDays, from, to, Stockholm);

    private static DateTime Utc(string instant) => DateTime.Parse(instant, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
}
