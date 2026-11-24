using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Lists;
using Staffora.Core.Models;

namespace Staffora.Test.Unit.Scheduling;

public class StaffPatternCoverTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    [Fact]
    public void The_cover_is_the_pattern_in_force_today_else_the_next_and_none_once_every_one_has_ended()
    {
        var current = StaffPatternCover.Of([("Ended", new DateOnly(2026, 1, 1), Today.AddDays(-1)), ("Later", Today.AddDays(20), null), ("Now", Today.AddDays(-16), null)], Today)!;
        Assert.Equal(("Now", true, "Now · since 2026-11-02"), (current.Pattern, current.Current, current.Label));

        var next = StaffPatternCover.Of([("Later", Today.AddDays(20), null), ("Sooner", Today.AddDays(13), Today.AddDays(19))], Today)!;
        Assert.Equal(("Sooner", false, "From 2026-12-01: Sooner"), (next.Pattern, next.Current, next.Label));

        Assert.Null(StaffPatternCover.Of([("Ended", new DateOnly(2026, 1, 1), Today.AddDays(-1))], Today));
        Assert.Null(StaffPatternCover.Of([], Today));
    }

    [Fact]
    public void A_pattern_in_force_with_an_end_says_when_it_ends_and_whether_that_is_soon()
    {
        var soon = new StaffPatternCover("Weekdays", Today.AddDays(-30), Today.AddDays(StaffPatternCover.EndingSoonDays), Today);
        Assert.Equal(("Weekdays · ends 2026-12-18", true), (soon.Label, soon.EndsSoon));

        var later = soon with { To = Today.AddDays(StaffPatternCover.EndingSoonDays + 1) };
        Assert.False(later.EndsSoon);

        Assert.False(new StaffPatternCover("Weekdays", Today.AddDays(1), Today.AddDays(2), Today).EndsSoon);

        Assert.True(new StaffPatternCover("Weekdays", Today.AddDays(-3), Today, Today).Current);
    }

    [Fact]
    public void The_rows_say_their_staff_counts_and_dates_in_words()
    {
        var pattern = new WorkPatternListItem(Guid.NewGuid(), "Weekdays", null, 1, WorkPatternState.Published, 5, TimeSpan.FromHours(40));
        Assert.Equal("None", pattern.StaffLabel);
        Assert.Equal("4 today", (pattern with { StaffToday = 4 }).StaffLabel);
        Assert.Equal("2 upcoming", (pattern with { StaffUpcoming = 2 }).StaffLabel);
        Assert.Equal("4 today · 2 upcoming", (pattern with { StaffToday = 4, StaffUpcoming = 2 }).StaffLabel);

        PatternStaffRow Row(DateOnly from, DateOnly? to) => new(Guid.NewGuid(), Guid.NewGuid(), "Kim Example", from, to, ResourcePatternAssignmentState.Active, Today);
        Assert.Equal(("Current", "2026-11-02 until further notice"), (Row(Today.AddDays(-16), null).WhenLabel, Row(Today.AddDays(-16), null).PeriodLabel));
        Assert.Equal("Upcoming", Row(Today.AddDays(1), null).WhenLabel);
        Assert.Equal(("Ended", "2026-11-01 to 2026-11-17"), (Row(new DateOnly(2026, 11, 1), Today.AddDays(-1)).WhenLabel, Row(new DateOnly(2026, 11, 1), Today.AddDays(-1)).PeriodLabel));

        var choice = new PatternStaffChoice(Guid.NewGuid(), "Sam Example", "sam@example.test", null);
        Assert.Equal("Sam Example (No work pattern)", choice.Label);
        Assert.Equal("Sam Example (From 2026-12-01: Weekdays)", (choice with { Cover = new StaffPatternCover("Weekdays", Today.AddDays(13), null, Today) }).Label);
    }

    [Fact]
    public void The_staff_list_keeps_its_work_pattern_filter_and_sort_in_the_address()
    {
        var query = StaffList.Definition.Default.WithFilter(StaffList.Pattern, StaffList.PatternNone) with { Sort = StaffList.SortPattern };
        var address = StaffList.Definition.Write(query);
        Assert.Equal(("none", "pattern"), (address[StaffList.Pattern], address[ListDefinition.SortKey]));

        var read = StaffList.Definition.Read(key => address.TryGetValue(key, out var value) ? value : null);
        Assert.Equal((StaffList.PatternNone, StaffList.SortPattern), (read.Filter(StaffList.Pattern), read.Sort));

        Assert.Null(StaffList.Definition.Read(key => key == StaffList.Pattern ? "someday" : null).Filter(StaffList.Pattern));
    }
}
