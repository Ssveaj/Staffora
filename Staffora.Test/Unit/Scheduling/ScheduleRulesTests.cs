using Staffora.Core.Scheduling;

namespace Staffora.Test.Unit.Scheduling;

public class ScheduleRulesTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData("2026-11-09", "2026-11-09")]
    [InlineData("2026-11-10", "2026-11-09")]
    [InlineData("2026-11-14", "2026-11-09")]
    [InlineData("2026-11-15", "2026-11-09")]
    [InlineData("2027-01-01", "2026-12-28")]
    public void The_Monday_of_a_day_is_the_first_day_of_its_week(string day, string monday)
    {
        Assert.Equal(DateOnly.Parse(monday, System.Globalization.CultureInfo.InvariantCulture), ScheduleRules.MondayOf(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Dates_in_order_with_a_Monday_anchor_are_valid_and_each_problem_is_named()
    {
        var monday = new DateOnly(2026, 11, 9);

        Assert.Empty(ScheduleRules.Problems(monday, null, monday));
        Assert.Empty(ScheduleRules.Problems(monday.AddDays(3), monday.AddDays(3), monday));
        Assert.Equal([ScheduleRules.EndsBeforeStart], ScheduleRules.Problems(monday.AddDays(3), monday.AddDays(2), monday));
        Assert.Equal([ScheduleRules.AnchorNotMonday], ScheduleRules.Problems(monday, null, monday.AddDays(1)));
        Assert.Equal([ScheduleRules.EndsBeforeStart, ScheduleRules.AnchorNotMonday], ScheduleRules.Problems(monday, monday.AddDays(-1), monday.AddDays(6)));
        Assert.Equal(WorkPatternRules.AnchorNotMonday, ScheduleRules.AnchorNotMonday);
    }

    [Fact]
    public void Two_assignments_overlap_when_they_share_a_day_and_an_open_end_runs_on_for_ever()
    {
        var autumn = Dates(First, "2026-08-17", "2026-12-31");

        Assert.True(ScheduleRules.Overlaps(autumn, Dates(Second, "2026-12-31", "2027-06-30")));
        Assert.True(ScheduleRules.Overlaps(autumn, Dates(Second, "2026-01-01", "2026-08-17")));
        Assert.True(ScheduleRules.Overlaps(autumn, Dates(Second, "2026-09-01", "2026-09-01")));
        Assert.True(ScheduleRules.Overlaps(Dates(First, "2026-08-17", null), Dates(Second, "2030-01-01", null)));
        Assert.False(ScheduleRules.Overlaps(autumn, Dates(Second, "2027-01-01", null)));
        Assert.False(ScheduleRules.Overlaps(autumn, Dates(Second, "2026-01-01", "2026-08-16")));
    }

    [Fact]
    public void Ending_an_assignment_on_a_day_and_starting_the_next_the_day_after_leaves_no_gap_and_no_overlap()
    {
        var current = Dates(First, "2026-08-17", null);
        var ended = current with { To = new DateOnly(2026, 12, 31) };
        var next = Dates(Second, "2027-01-01", null);

        Assert.Single(ScheduleRules.OverlapsOf(next, [current]));
        Assert.Equal(next.From, ScheduleRules.DayAfter(ended));
        Assert.Empty(ScheduleRules.OverlapsOf(next, [ended]));
        Assert.Null(ScheduleRules.DayAfter(current));

        for (var day = new DateOnly(2026, 8, 17); day <= new DateOnly(2027, 3, 1); day = day.AddDays(1))
        {
            Assert.Single(new[] { ended, next }, dates => dates.Covers(day));
        }
    }

    [Fact]
    public void An_overlap_is_refused_with_the_assignment_it_overlaps_named_and_the_candidate_itself_is_left_out()
    {
        var candidate = Dates(First, "2026-11-09", null);
        var other = Dates(Second, "2026-08-17", "2026-12-31") with { Label = "Full time" };

        Assert.Equal(
            ["It overlaps the active assignment of Full time from 2026-08-17 to 2026-12-31: a staff member works by one pattern on any date. End that assignment before this one starts, or start this one after it ends."],
            ScheduleRules.OverlapReasons(candidate, [other, candidate with { From = new DateOnly(2020, 1, 1) }]));
        Assert.Empty(ScheduleRules.OverlapReasons(candidate, [other with { To = new DateOnly(2026, 11, 8) }]));
        Assert.Equal("from 2026-11-09 until further notice", ScheduleRules.Describe(candidate));
    }

    [Fact]
    public void The_assignment_of_a_day_is_the_one_that_covers_it_the_latest_start_and_then_the_lowest_id_winning()
    {
        var older = Dates(Second, "2026-08-17", null);
        var newer = Dates(First, "2026-11-02", "2026-11-30");
        var twin = Dates(Second, "2026-11-02", "2026-11-30");

        Assert.Null(ScheduleRules.AssignmentOn(new DateOnly(2026, 8, 16), [older, newer]));
        Assert.Equal(older, ScheduleRules.AssignmentOn(new DateOnly(2026, 10, 1), [older, newer]));
        Assert.Equal(newer, ScheduleRules.AssignmentOn(new DateOnly(2026, 11, 2), [older, newer]));
        Assert.Equal(older, ScheduleRules.AssignmentOn(new DateOnly(2026, 12, 1), [older, newer]));
        Assert.Equal(newer, ScheduleRules.AssignmentOn(new DateOnly(2026, 11, 10), [twin, newer]));
        Assert.Equal(newer, ScheduleRules.AssignmentOn(new DateOnly(2026, 11, 10), [newer, twin]));
    }

    private static ScheduledDates Dates(Guid id, string from, string? to) =>
        new(id, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture));
}
