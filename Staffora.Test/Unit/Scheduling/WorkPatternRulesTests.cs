using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Test.Unit.Scheduling;

public class WorkPatternRulesTests
{
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Twelve = new(12, 0);
    private static readonly TimeOnly Sixteen = new(16, 0);

    [Fact]
    public void A_weekly_pattern_of_split_days_with_touching_segments_is_valid()
    {
        PatternSegment[] segments = [new(0, Eight, Twelve), new(0, Twelve, Sixteen), new(1, Eight, Sixteen), new(6, Eight, Twelve)];

        Assert.Empty(WorkPatternRules.Problems(1, segments));
    }

    [Fact]
    public void Overlapping_segments_of_a_day_are_named_with_their_times()
    {
        PatternSegment[] segments = [new(2, Eight, Twelve), new(2, new TimeOnly(11, 30), Sixteen), new(3, Eight, Twelve)];

        Assert.Equal(["Wednesday: 08:00-12:00 and 11:30-16:00 overlap."], WorkPatternRules.Problems(1, segments));
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(13, 12)]
    public void A_segment_that_does_not_start_before_it_ends_is_refused(int startHour, int endHour)
    {
        PatternSegment[] segments = [new(8, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0))];

        Assert.Equal([$"Week 2, Tuesday: a segment ({startHour:00}:00-{endHour:00}:00) must start before it ends."], WorkPatternRules.Problems(2, segments));
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(1, -1)]
    [InlineData(2, 14)]
    public void A_segment_on_a_day_outside_the_cycle_is_refused(int cycleWeeks, int dayIndex)
    {
        Assert.Equal([$"A segment (08:00-12:00) is on a day outside the {cycleWeeks}-week cycle."], WorkPatternRules.Problems(cycleWeeks, [new PatternSegment(dayIndex, Eight, Twelve)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void A_cycle_is_one_to_eight_weeks(int cycleWeeks)
    {
        Assert.Equal([WorkPatternRules.CycleOutOfRange], WorkPatternRules.Problems(cycleWeeks, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkPatternRules.Expand(cycleWeeks, [], new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 10)).ToList());
    }

    [Theory]
    [InlineData(0, 1, "Monday")]
    [InlineData(6, 1, "Sunday")]
    [InlineData(0, 2, "Week 1, Monday")]
    [InlineData(13, 2, "Week 2, Sunday")]
    [InlineData(15, 3, "Week 3, Tuesday")]
    public void A_day_of_the_cycle_is_named_by_its_weekday_and_its_week_in_a_longer_cycle(int dayIndex, int cycleWeeks, string label)
    {
        Assert.Equal(label, WorkPatternRules.DayLabel(dayIndex, cycleWeeks));
    }

    [Fact]
    public void A_weekly_pattern_repeats_every_week_from_its_anchor_in_date_and_time_order()
    {
        PatternSegment[] segments = [new(0, new TimeOnly(13, 0), Sixteen), new(0, Eight, Twelve), new(2, Eight, Sixteen)];
        var monday = new DateOnly(2027, 1, 4);

        var expanded = WorkPatternRules.Expand(1, segments, monday, monday, monday.AddDays(13)).ToList();

        Assert.Equal(
        [
            new WorkingSegment(monday, Eight, Twelve),
            new WorkingSegment(monday, new TimeOnly(13, 0), Sixteen),
            new WorkingSegment(monday.AddDays(2), Eight, Sixteen),
            new WorkingSegment(monday.AddDays(7), Eight, Twelve),
            new WorkingSegment(monday.AddDays(7), new TimeOnly(13, 0), Sixteen),
            new WorkingSegment(monday.AddDays(9), Eight, Sixteen),
        ], expanded);
    }

    [Fact]
    public void A_rotation_follows_its_anchor_in_both_directions_and_an_empty_range_gives_nothing()
    {
        PatternSegment[] segments = [new(0, Eight, Twelve), new(8, Twelve, Sixteen)];
        var anchor = new DateOnly(2027, 1, 4);

        var dates = WorkPatternRules.Expand(2, segments, anchor, anchor.AddDays(-14), anchor.AddDays(20)).Select(segment => segment.Date).ToList();

        Assert.Equal([anchor.AddDays(-14), anchor.AddDays(-6), anchor, anchor.AddDays(8), anchor.AddDays(14)], dates);
        Assert.Empty(WorkPatternRules.Expand(2, segments, anchor, anchor.AddDays(1), anchor));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(10)]
    public void A_cycle_anchored_on_any_day_but_a_Monday_is_refused_so_no_weekday_gets_another_days_hours(int day)
    {
        PatternSegment[] mondays = [new(0, Eight, Sixteen)];
        var anchor = new DateOnly(2027, 1, day);

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => WorkPatternRules.Expand(1, mondays, anchor, anchor, anchor.AddDays(6)).ToList());

        Assert.Equal("anchor", refusal.ParamName);
        Assert.StartsWith(WorkPatternRules.AnchorNotMonday, refusal.Message, StringComparison.Ordinal);
        Assert.All(WorkPatternRules.Expand(1, mondays, new DateOnly(2027, 1, 4), anchor, anchor.AddDays(6)), segment => Assert.Equal(DayOfWeek.Monday, segment.Date.DayOfWeek));
    }

    [Fact]
    public void The_workflow_edits_a_pattern_only_as_a_draft_deletes_only_a_draft_and_archives_a_published_one()
    {
        var workflow = new WorkPattern().GetWorkflow<WorkPatternState, WorkPattern>();

        Assert.Equal(FieldAccessFlags.ReadWrite, workflow.GetFieldConfig(WorkPatternState.Draft).GetAreaAccess(WorkPatternWorkflowAttribute.AreaSegments));
        foreach (var kept in new[] { WorkPatternState.Published, WorkPatternState.Archived })
        {
            Assert.Equal(FieldAccessFlags.ReadOnly, workflow.GetFieldConfig(kept).GetAreaAccess(WorkPatternWorkflowAttribute.AreaSegments));
            Assert.Equal(FieldAccessFlags.ReadOnly, workflow.GetFieldConfig(kept).GetFieldAccess(nameof(WorkPattern.Name)));
        }

        Assert.Equal([WorkPatternState.Draft], workflow.GetTransitionsFrom(WorkPatternState.Draft).Where(step => step.To == WorkPatternState.Deleted).Select(step => step.From));
        Assert.DoesNotContain(workflow.GetTransitionsFrom(WorkPatternState.Published), step => step.To == WorkPatternState.Deleted);
        Assert.Equal([WorkPatternState.Archived], workflow.GetTransitionsFrom(WorkPatternState.Published).Select(step => step.To));
        Assert.Empty(workflow.GetTransitionsFrom(WorkPatternState.Archived));
    }
}
