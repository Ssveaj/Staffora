using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class AbsenceImpactRulesTests
{
    private static readonly DateTime Start = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private static readonly JobTimes Mornings = JobTimes.Recurring(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 31), [new OccurrenceTimes(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)), new OccurrenceTimes(Weekday.Wednesday, new TimeOnly(8, 0), new TimeOnly(10, 0))]);

    [Fact]
    public void An_absence_affects_the_remaining_times_of_a_job_it_overlaps_by_the_same_instants_as_the_availability_rule()
    {
        var sick = Absence(new DateOnly(2027, 3, 8), null, new DateOnly(2027, 3, 10), null);
        Assert.Equal([new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 10)], AbsenceImpactRules.AffectedOccurrences(sick, Mornings, Start, TimeZoneInfo.Utc).Select(occurrence => occurrence.Date));

        var afternoon = Absence(new DateOnly(2027, 3, 8), new TimeOnly(12, 0), new DateOnly(2027, 3, 8), null);
        Assert.Empty(AbsenceImpactRules.AffectedOccurrences(afternoon, Mornings, Start, TimeZoneInfo.Utc));
        var touching = Absence(new DateOnly(2027, 3, 8), new TimeOnly(9, 59), new DateOnly(2027, 3, 8), null);
        Assert.Single(AbsenceImpactRules.AffectedOccurrences(touching, Mornings, Start, TimeZoneInfo.Utc));

        var duringTheFirst = new DateTime(2027, 3, 8, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal([new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 10)], AbsenceImpactRules.AffectedOccurrences(sick, Mornings, duringTheFirst, TimeZoneInfo.Utc).Select(occurrence => occurrence.Date));
        var afterTheFirst = new DateTime(2027, 3, 8, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal([new DateOnly(2027, 3, 10)], AbsenceImpactRules.AffectedOccurrences(sick, Mornings, afterTheFirst, TimeZoneInfo.Utc).Select(occurrence => occurrence.Date));

        var night = JobTimes.Single(new DateOnly(2027, 3, 7), new TimeOnly(22, 0), new TimeOnly(6, 0));
        Assert.Single(AbsenceImpactRules.AffectedOccurrences(Absence(new DateOnly(2027, 3, 8), null, new DateOnly(2027, 3, 8), null), night, Start, TimeZoneInfo.Utc));
        Assert.Empty(AbsenceImpactRules.AffectedOccurrences(Absence(new DateOnly(2027, 3, 9), new TimeOnly(10, 0), new DateOnly(2027, 3, 8), null), Mornings, Start, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_slot_is_open_again_from_a_take_off_until_a_later_assignment_holds_it_and_never_more_often_than_it_is_open()
    {
        var at = new DateTime(2027, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, AbsenceImpactRules.OpenAgain([], [], 1));
        Assert.Equal(1, AbsenceImpactRules.OpenAgain([at], [], 1));
        Assert.Equal(1, AbsenceImpactRules.OpenAgain([at], [at.AddHours(-1)], 1));
        Assert.Equal(0, AbsenceImpactRules.OpenAgain([at], [at.AddHours(1)], 1));
        Assert.Equal(0, AbsenceImpactRules.OpenAgain([at], [], 0));
        Assert.Equal(1, AbsenceImpactRules.OpenAgain([at, at.AddHours(2)], [at.AddHours(1)], 2));
        Assert.Equal(1, AbsenceImpactRules.OpenAgain([at, at.AddMinutes(1)], [], 1));
        Assert.Equal(2, AbsenceImpactRules.OpenAgain([at, at.AddMinutes(1)], [], 3));

        Assert.Equal(("", "open again", "1 open again"), (AbsenceImpactRules.OpenAgainText(0, 1), AbsenceImpactRules.OpenAgainText(1, 1), AbsenceImpactRules.OpenAgainText(1, 2)));
    }

    private static ScheduledAbsence Absence(DateOnly first, TimeOnly? start, DateOnly last, TimeOnly? end) =>
        new(Guid.NewGuid(), Guid.NewGuid(), AbsenceKind.SickLeave, ResourceAbsenceState.Requested, first, start, last, end);
}
