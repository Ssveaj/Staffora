using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class WorkPatternLifecycleTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public WorkPatternLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_a_draft_removes_it_with_its_segments()
    {
        var (scenario, admin) = await ArrangeAsync("Deleting Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(admin));
        var patterns = host.Get<WorkPatternService>();
        var kept = await CreateDraftAsync(patterns, "Kept", Weekdays(new TimeOnly(9, 0), new TimeOnly(15, 0)));
        var id = await CreateDraftAsync(patterns, "Discarded", Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        Assert.Equal(5, await SegmentCountAsync(id));

        var deleted = await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!, WorkPatternWorkflowAttribute.DeleteStep, null, Ct);
        Assert.True(deleted.IsSuccess, deleted.Error);

        Assert.Null(await StoredAsync(id));
        Assert.Equal(0, await SegmentCountAsync(id));
        Assert.Equal([WorkPatternWorkflowAttribute.SaveStep, WorkPatternWorkflowAttribute.DeleteStep], await HistoryAsync(id));
        Assert.Equal(5, await SegmentCountAsync(kept));
    }

    [Fact]
    public async Task A_published_pattern_changes_no_more_and_is_only_archived()
    {
        var (scenario, admin) = await ArrangeAsync("Freezing Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(admin));
        var patterns = host.Get<WorkPatternService>();
        var id = await CreateDraftAsync(patterns, "Mornings", Weekdays(new TimeOnly(8, 0), new TimeOnly(12, 0)));
        Assert.True((await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!, WorkPatternWorkflowAttribute.PublishStep, null, Ct)).IsSuccess);

        Assert.False((await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!, WorkPatternWorkflowAttribute.DeleteStep, null, Ct)).IsSuccess);

        var updater = (await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!;
        updater.Entity.Segments.First().End = new TimeOnly(13, 0);
        Assert.False((await patterns.UpdateWorkPatternAndTransitionAsync(updater, WorkPatternWorkflowAttribute.ArchiveStep, null, Ct)).IsSuccess);
        updater = (await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!;
        updater.Entity.Name = "Renamed";
        Assert.False((await patterns.UpdateWorkPatternAndTransitionAsync(updater, WorkPatternWorkflowAttribute.ArchiveStep, null, Ct)).IsSuccess);
        var stored = (await StoredAsync(id))!;
        Assert.Equal("Mornings", stored.Name);
        Assert.All(stored.Segments, segment => Assert.Equal(new TimeOnly(12, 0), segment.End));

        Assert.True((await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!, WorkPatternWorkflowAttribute.ArchiveStep, null, Ct)).IsSuccess);
        Assert.Equal(WorkPatternState.Archived, (await StoredAsync(id))!.State);
        Assert.Equal(5, await SegmentCountAsync(id));
    }

    [Fact]
    public async Task A_save_with_overlapping_or_reversed_segments_is_refused_with_what_is_wrong()
    {
        var (scenario, admin) = await ArrangeAsync("Checking Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(admin));
        var patterns = host.Get<WorkPatternService>();

        var overlapping = await patterns.CreateWorkPatternAsync(Pattern("Clash", 1, new PatternSegment(0, new TimeOnly(8, 0), new TimeOnly(12, 0)), new PatternSegment(0, new TimeOnly(11, 0), new TimeOnly(15, 0))), WorkPatternWorkflowAttribute.SaveStep, Ct);
        Assert.False(overlapping.IsSuccess);
        Assert.Contains("Monday: 08:00-12:00 and 11:00-15:00 overlap.", overlapping.Error, StringComparison.Ordinal);
        Assert.Empty((await patterns.QueryWorkPatternsAsync(new ListQuery(), Ct)).Value!.Rows);

        var id = await CreateDraftAsync(patterns, "Two weeks", [new PatternSegment(0, new TimeOnly(8, 0), new TimeOnly(12, 0)), new PatternSegment(8, new TimeOnly(9, 0), new TimeOnly(17, 0))], cycleWeeks: 2);
        var updater = (await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!;
        updater.Entity.Segments.Single(segment => segment.DayIndex == 8).End = new TimeOnly(7, 0);
        var reversed = await patterns.UpdateWorkPatternAndTransitionAsync(updater, WorkPatternWorkflowAttribute.SaveStep, null, Ct);
        Assert.Contains("Week 2, Tuesday: a segment (09:00-07:00) must start before it ends.", reversed.Error, StringComparison.Ordinal);
        updater = (await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!;
        updater.Entity.CycleWeeks = 1;
        var shortened = await patterns.UpdateWorkPatternAndTransitionAsync(updater, WorkPatternWorkflowAttribute.SaveStep, null, Ct);
        Assert.Contains("A segment (09:00-17:00) is on a day outside the 1-week cycle.", shortened.Error, StringComparison.Ordinal);
        var stored = (await StoredAsync(id))!;
        Assert.Equal(2, stored.CycleWeeks);
        Assert.Equal(new TimeOnly(17, 0), stored.Segments.Single(segment => segment.DayIndex == 8).End);

        var empty = await CreateDraftAsync(patterns, "Empty", []);
        var publishEmpty = await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(empty, Ct)).Value!, WorkPatternWorkflowAttribute.PublishStep, null, Ct);
        Assert.Contains(WorkPatternWorkflowAttribute.HasSegmentsReason, publishEmpty.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copying_a_published_pattern_to_a_new_draft_leaves_it_as_it_is()
    {
        var (scenario, admin) = await ArrangeAsync("Copying Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(admin));
        var patterns = host.Get<WorkPatternService>();
        var source = await CreateDraftAsync(patterns, "Rotation", [new PatternSegment(0, new TimeOnly(8, 0), new TimeOnly(16, 0)), new PatternSegment(7, new TimeOnly(10, 0), new TimeOnly(18, 0))], cycleWeeks: 2);
        Assert.True((await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(source, Ct)).Value!, WorkPatternWorkflowAttribute.PublishStep, null, Ct)).IsSuccess);

        var copy = await patterns.NewWorkPatternAsync(source, Ct);
        Assert.True(copy.IsSuccess, copy.Error);
        Assert.Equal(WorkPatternState.Draft, copy.Value!.State);
        Assert.Equal("Rotation (copy)", copy.Value.Name);
        Assert.Equal(2, copy.Value.CycleWeeks);
        Assert.Equal([(0, new TimeOnly(8, 0)), (7, new TimeOnly(10, 0))], copy.Value.Segments.Select(segment => (segment.DayIndex, segment.Start)));
        Assert.Single((await patterns.QueryWorkPatternsAsync(new ListQuery(), Ct)).Value!.Rows);

        copy.Value.Segments.First().End = new TimeOnly(14, 0);
        var created = await patterns.CreateWorkPatternAsync(copy.Value, WorkPatternWorkflowAttribute.SaveStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        Assert.Equal(WorkPatternState.Draft, (await StoredAsync(created.Value))!.State);
        Assert.Equal(new TimeOnly(14, 0), (await StoredAsync(created.Value))!.Segments.Single(segment => segment.DayIndex == 0).End);
        var original = (await StoredAsync(source))!;
        Assert.Equal(WorkPatternState.Published, original.State);
        Assert.Equal(new TimeOnly(16, 0), original.Segments.Single(segment => segment.DayIndex == 0).End);
    }

    [Fact]
    public async Task Whoever_only_assigns_patterns_reads_them_and_makes_none()
    {
        var (scenario, admin) = await ArrangeAsync("Assigning Staffing");
        await using var maker = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(admin));
        var id = await CreateDraftAsync(maker.Get<WorkPatternService>(), "Evenings", Weekdays(new TimeOnly(16, 0), new TimeOnly(20, 0)));
        var assigner = await scenario.AddPersonAsync(this.database, "Assigner", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, assigner, Ct, RolePermission.AssignWorkPatterns);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(assigner));
        var patterns = host.Get<WorkPatternService>();

        Assert.Single((await patterns.QueryWorkPatternsAsync(new ListQuery(), Ct)).Value!.Rows);
        Assert.Equal(new WorkPatternEditorOptions(FieldsLocked: true, MayCopy: false) { ReadsStaff = true }, (await patterns.GetWorkPatternEditorOptionsAsync(id, Ct)).Value);
        Assert.Equal(WorkPatternService.NoManage, (await patterns.NewWorkPatternAsync(id, Ct)).Error);
        Assert.Equal(WorkPatternService.NoManage, (await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(id, Ct)).Value!, WorkPatternWorkflowAttribute.DeleteStep, null, Ct)).Error);
        Assert.NotNull(await StoredAsync(id));

        var other = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName("Other Assigning Staffing"));
        await using var ofOther = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime());
        Assert.Equal(WorkPatternService.NotFound, (await ofOther.Get<WorkPatternService>().LoadWorkPatternUpdaterAsync(id, Ct)).Error);
        Assert.Equal(WorkPatternService.NotFound, (await ofOther.Get<WorkPatternService>().NewWorkPatternAsync(id, Ct)).Error);
    }

    private async Task<(StaffScenario Scenario, Guid Admin)> ArrangeAsync(string name)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName(name));
        var admin = await scenario.AddPersonAsync(this.database, "Alex", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(admin, scenario.RootId, scenario.Onboarded.Roles.Administrator, scenario.TenantId)).IsSuccess);
        return (scenario, admin);
    }

    private static PatternSegment[] Weekdays(TimeOnly start, TimeOnly end, TimeOnly? friday = null) =>
        [.. Enumerable.Range(0, 5).Select(day => new PatternSegment(day, start, day == 4 && friday is { } shorter ? shorter : end))];

    private static WorkPattern Pattern(string name, int cycleWeeks, params PatternSegment[] segments)
    {
        var pattern = new WorkPattern { Name = name, CycleWeeks = cycleWeeks };
        foreach (var segment in segments)
        {
            pattern.Segments.Add(new WorkPatternSegment { DayIndex = segment.DayIndex, Start = segment.Start, End = segment.End });
        }

        return pattern;
    }

    private async Task<Guid> CreateDraftAsync(WorkPatternService patterns, string name, PatternSegment[] segments, int cycleWeeks = 1)
    {
        var created = await patterns.CreateWorkPatternAsync(Pattern(name, cycleWeeks, segments), WorkPatternWorkflowAttribute.SaveStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<WorkPattern?> StoredAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.WorkPatterns.AsNoTracking().Include(pattern => pattern.Segments).FirstOrDefaultAsync(pattern => pattern.Id == id, Ct);
    }

    private async Task<int> SegmentCountAsync(Guid patternId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.WorkPatternSegments.CountAsync(segment => segment.WorkPatternId == patternId, Ct);
    }

    private async Task<List<string>> HistoryAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityType == nameof(WorkPattern) && entry.EntityId == id).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).Select(entry => entry.TransitionKey).ToListAsync(Ct);
    }
}
