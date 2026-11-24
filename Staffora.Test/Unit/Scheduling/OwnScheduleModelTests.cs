using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;

namespace Staffora.Test.Unit.Scheduling;

public class OwnScheduleModelTests
{
    private static readonly DateTime Asked = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_absence_says_its_kind_where_it_stands_and_the_part_of_the_day_it_covers()
    {
        var id = Guid.NewGuid();
        Assert.Equal(("Sick leave, reported", "All day", true), Words(new OwnAbsenceEntry(id, AbsenceKind.SickLeave, ResourceAbsenceState.Requested, null, null)));
        Assert.Equal(("Planned leave, waiting for approval", "From 13:00", false), Words(new OwnAbsenceEntry(id, AbsenceKind.PlannedLeave, ResourceAbsenceState.Requested, new TimeOnly(13, 0), null)));
        Assert.Equal(("Planned leave, approved", "Until 12:00", true), Words(new OwnAbsenceEntry(id, AbsenceKind.PlannedLeave, ResourceAbsenceState.Approved, null, new TimeOnly(12, 0))));
        Assert.Equal(("Sick leave, approved", "09:00–11:30", true), Words(new OwnAbsenceEntry(id, AbsenceKind.SickLeave, ResourceAbsenceState.Approved, new TimeOnly(9, 0), new TimeOnly(11, 30))));

        static (string, string, bool) Words(OwnAbsenceEntry entry) => (entry.Label, entry.TimesLabel, entry.Blocks);
    }

    [Theory]
    [InlineData(SlotAssignmentState.Requested, "You are asked", "Your answer is needed")]
    [InlineData(SlotAssignmentState.Accepted, "You said yes", "You said yes, and the coordinator picks who works")]
    [InlineData(SlotAssignmentState.Assigned, "You are assigned", "You are assigned")]
    public void An_occurrence_and_a_row_say_what_the_assignment_is_to_the_staff_member(SlotAssignmentState state, string occurrence, string next)
    {
        Assert.Equal(occurrence, new OwnOccurrenceEntry(Guid.NewGuid(), "Algebra", "Lead", state, new TimeOnly(8, 0), new TimeOnly(9, 30)).StateLabel);
        var row = new OwnAssignmentRow(Guid.NewGuid(), "Algebra", "Lead", "Brightpath Learning", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), state, Asked);
        Assert.Equal((next, "Algebra: Lead", "2027-02-01 to 2027-06-11"), (row.NextLabel, row.Label, row.PeriodLabel));
    }

    [Theory]
    [InlineData(SlotAssignmentState.Declined, "You said no")]
    [InlineData(SlotAssignmentState.NotSelected, "Another staff member was assigned")]
    [InlineData(SlotAssignmentState.Canceled, "Canceled")]
    public void A_closed_row_says_how_it_closed(SlotAssignmentState state, string next) =>
        Assert.Equal(next, new OwnAssignmentRow(Guid.NewGuid(), "Algebra", "Lead", "Brightpath Learning", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), state, Asked).NextLabel);

    [Fact]
    public void A_schedule_reads_any_week_but_the_calendars_first_and_last()
    {
        Assert.True(Staffora.Core.Scheduling.ScheduleRules.HasReadableWeek(new DateOnly(2027, 3, 10)));
        Assert.True(Staffora.Core.Scheduling.ScheduleRules.HasReadableWeek(DateOnly.MinValue.AddDays(8)));
        Assert.True(Staffora.Core.Scheduling.ScheduleRules.HasReadableWeek(DateOnly.MaxValue.AddDays(-8)));
        Assert.False(Staffora.Core.Scheduling.ScheduleRules.HasReadableWeek(DateOnly.MinValue.AddDays(7)));
        Assert.False(Staffora.Core.Scheduling.ScheduleRules.HasReadableWeek(DateOnly.MaxValue.AddDays(-7)));
    }

    [Fact]
    public void A_week_names_its_days_marks_today_and_counts_its_hours()
    {
        var monday = new DateOnly(2027, 3, 8);
        var segment = new Staffora.Core.Scheduling.WorkingTime(monday, new TimeOnly(8, 0), new TimeOnly(12, 30), new DateTime(2027, 3, 8, 7, 0, 0, DateTimeKind.Utc), new DateTime(2027, 3, 8, 11, 30, 0, DateTimeKind.Utc), Guid.NewGuid(), Guid.NewGuid());
        var days = Enumerable.Range(0, 7).Select(offset => new OwnScheduleDay(monday.AddDays(offset), offset == 0 ? [segment] : [], [], [])).ToList();
        var week = new OwnScheduleWeek(monday, new DateOnly(2027, 3, 10), "Europe/Stockholm", days);

        Assert.Equal(("2027-03-08 to 2027-03-14", new DateOnly(2027, 3, 14), true, "4.5 h"), (week.Label, week.Sunday, week.IsCurrent, week.WorkedLabel));
        Assert.Equal(("Monday 2027-03-08", "08:00–12:30"), (days[0].Label, days[0].HoursLabel));
        Assert.Equal("No working hours", days[1].HoursLabel);
        Assert.False(new OwnScheduleWeek(monday.AddDays(7), new DateOnly(2027, 3, 10), "Europe/Stockholm", days).IsCurrent);
    }
}
