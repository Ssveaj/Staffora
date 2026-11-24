using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;

namespace Staffora.Test.Unit.Scheduling;

public class OwnScheduleMonthTests
{
    private static readonly DateOnly Monday = new(2026, 11, 23);

    [Fact]
    public void Over_working_time_absence_covers_first_then_an_assignment_and_the_rest_is_open()
    {
        var day = Day(Monday, [(6, 0, 10, 0), (15, 30, 19, 0)],
            occurrences: [(6, 0, 9, 0), (15, 30, 19, 0), (20, 0, 21, 0)],
            absences: [new OwnAbsenceEntry(Guid.NewGuid(), AbsenceKind.PlannedLeave, ResourceAbsenceState.Approved, new TimeOnly(15, 30), null)]);

        var hours = OwnHours.Of([day]);

        Assert.Equal((450, 180, 210, 60), (hours.WorkingMinutes, hours.AssignmentMinutes, hours.AbsenceMinutes, hours.OpenMinutes));
        Assert.Equal(("7.5 h", "3 h", "3.5 h", "1 h"), (hours.WorkingLabel, hours.AssignmentLabel, hours.AbsenceLabel, hours.OpenLabel));

        var away = Day(Monday, [(8, 0, 16, 0)], occurrences: [(9, 0, 10, 0)], absences: [new OwnAbsenceEntry(Guid.NewGuid(), AbsenceKind.SickLeave, ResourceAbsenceState.Requested, null, null)]);
        Assert.Equal((480, 0, 480), (OwnHours.Of([away]).WorkingMinutes, OwnHours.Of([away]).AssignmentMinutes, OwnHours.Of([away]).AbsenceMinutes));
    }

    [Fact]
    public void The_axis_spans_whole_hours_around_the_month_and_folds_an_empty_stretch_of_two_hours_or_more()
    {
        var axis = OwnDayAxis.Of([
            Day(Monday, [(6, 0, 10, 0), (15, 30, 19, 0)]),
            Day(Monday.AddDays(1), [(5, 30, 6, 0)], occurrences: [(17, 0, 19, 15)]),
        ]);

        Assert.Equal([new OwnAxisPiece(5 * 60, 10 * 60, false), new OwnAxisPiece(10 * 60, (15 * 60) + 30, true), new OwnAxisPiece((15 * 60) + 30, 20 * 60, false)], axis.Pieces);
        Assert.Equal("10:00–15:30 folded", axis.FoldsLabel);

        Assert.Equal(0, axis.Position(5 * 60));
        Assert.Equal(0.5, axis.Position(10 * 60));
        Assert.Equal(0.55, axis.Position((15 * 60) + 30));
        Assert.Equal(1, axis.Position(20 * 60));
        Assert.Equal(0, axis.Position(0));
        Assert.True(axis.IsFolded(12 * 60));
        Assert.False(axis.IsFolded(10 * 60));

        Assert.Equal(["05:00", "06:00", "07:00", "08:00", "09:00", "10:00", "15:30", "17:00", "18:00", "19:00"], axis.Ticks.Select(tick => tick.Label));
    }

    [Fact]
    public void A_month_with_nothing_in_it_has_the_usual_working_day_as_its_axis_and_a_timed_absence_widens_it()
    {
        Assert.Equal([new OwnAxisPiece(8 * 60, 17 * 60, false)], OwnDayAxis.Of([Day(Monday, [])]).Pieces);

        var late = Day(Monday, [(8, 0, 12, 0)], absences: [new OwnAbsenceEntry(Guid.NewGuid(), AbsenceKind.PlannedLeave, ResourceAbsenceState.Requested, new TimeOnly(9, 0), new TimeOnly(13, 0))]);
        Assert.Equal([new OwnAxisPiece(8 * 60, 13 * 60, false)], OwnDayAxis.Of([late]).Pieces);

        var whole = Day(Monday, [(8, 0, 12, 0)], absences: [new OwnAbsenceEntry(Guid.NewGuid(), AbsenceKind.PlannedLeave, ResourceAbsenceState.Approved, null, null)]);
        Assert.Equal([new OwnAxisPiece(8 * 60, 12 * 60, false)], OwnDayAxis.Of([whole]).Pieces);
    }

    [Fact]
    public void A_pattern_tells_its_week_of_the_rotation_the_weeks_it_gives_and_the_days_left_of_it()
    {
        var anchor = new DateOnly(2026, 8, 31);
        PatternSegment[] segments =
        [
            .. Enumerable.Range(0, 5).Select(day => new PatternSegment(day, new TimeOnly(6, 0), new TimeOnly(10, 0))),
            .. Enumerable.Range(7, 4).Select(day => new PatternSegment(day, new TimeOnly(6, 0), new TimeOnly(10, 0))),
            new PatternSegment(12, new TimeOnly(8, 0), new TimeOnly(11, 30)),
        ];
        var pattern = new OwnPatternPeriod(Guid.NewGuid(), "Split rotation", anchor, new DateOnly(2026, 12, 20), 2, anchor, true, segments);

        Assert.Equal(1, pattern.CycleWeekOn(new DateOnly(2026, 11, 23)));
        Assert.Equal(1, pattern.CycleWeekOn(new DateOnly(2026, 11, 29)));
        Assert.Equal(2, pattern.CycleWeekOn(new DateOnly(2026, 11, 30)));
        Assert.Equal(2, pattern.CycleWeekOn(new DateOnly(2026, 12, 4)));
        Assert.Equal(2, pattern.CycleWeekOn(anchor.AddDays(-1)));

        var rows = pattern.CycleRows;
        Assert.Equal([(1, 1200), (2, 1170)], rows.Select(row => (row.Week, row.Minutes)));
        Assert.Equal(["06:00–10:00"], OwnPatternWeekRow.TimesOf(rows[0].Days[0]));
        Assert.Empty(rows[0].Days[5]);
        Assert.Equal(["08:00–11:30"], OwnPatternWeekRow.TimesOf(rows[1].Days[5]));

        Assert.Equal(27, pattern.DaysLeft(new DateOnly(2026, 11, 23)));
        Assert.Equal(0, pattern.DaysLeft(new DateOnly(2026, 12, 20)));
        Assert.Null(pattern.DaysLeft(new DateOnly(2026, 12, 21)));
        Assert.Null((pattern with { To = null }).DaysLeft(new DateOnly(2026, 11, 23)));
        Assert.Equal("31 Aug 2026 to 20 Dec 2026", pattern.PeriodLabel);
        Assert.Equal("From 31 Aug 2026 until further notice", (pattern with { To = null }).PeriodLabel);
    }

    [Fact]
    public void A_month_names_itself_and_its_weeks_and_knows_whether_it_holds_today()
    {
        var first = new DateOnly(2026, 11, 1);
        var days = Enumerable.Range(0, 30).Select(offset => Day(first.AddDays(offset), [])).ToList();
        var weeks = days.GroupBy(day => ScheduleRules.MondayOf(day.Date)).Select(week => new OwnMonthWeek(week.Key, [.. week])).ToList();
        var month = new OwnScheduleMonth(first, new DateOnly(2026, 11, 22), "Europe/Stockholm", weeks);

        Assert.Equal(("November 2026", "2026-11", "Weeks 44 to 49", true), (month.Title, month.Key, month.WeeksLabel, month.IsCurrent));
        Assert.Equal(new DateOnly(2026, 11, 30), month.Last);
        Assert.Equal("1 Nov", weeks[0].RangeLabel);
        Assert.Equal("2 Nov to 8 Nov", weeks[1].RangeLabel);
        Assert.Equal("30 Nov", weeks[^1].RangeLabel);
        Assert.False((month with { Today = new DateOnly(2026, 12, 1) }).IsCurrent);
        Assert.Equal(first, OwnScheduleMonth.FirstOf(new DateOnly(2026, 11, 30)));
        Assert.False(OwnScheduleMonth.IsReadable(DateOnly.MinValue));
        Assert.False(OwnScheduleMonth.IsReadable(new DateOnly(9999, 12, 1)));
        Assert.True(OwnScheduleMonth.IsReadable(new DateOnly(2, 1, 1)));
    }

    private static OwnScheduleDay Day(DateOnly date, (int, int, int, int)[] working, (int, int, int, int)[]? occurrences = null, OwnAbsenceEntry[]? absences = null) => new(
        date,
        [.. working.Select(work => new WorkingTime(date, new TimeOnly(work.Item1, work.Item2), new TimeOnly(work.Item3, work.Item4), default, default, Guid.Empty, Guid.Empty))],
        absences ?? [],
        [.. (occurrences ?? []).Select(occurrence => new OwnOccurrenceEntry(Guid.NewGuid(), "Office cleaning", "Cleaner", SlotAssignmentState.Assigned, new TimeOnly(occurrence.Item1, occurrence.Item2), new TimeOnly(occurrence.Item3, occurrence.Item4)))]);
}
