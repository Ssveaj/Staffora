using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Workflow;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public partial class WorkflowDeleteTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public WorkflowDeleteTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Deleting_a_parent_removes_it_with_its_child_and_grandchild_and_writes_three_correlated_history_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (parent, child, grandchild) = await AddFamilyAsync(scenario.A, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-delete", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.AnyAsync(row => row.Id == parent, cancellationToken));
        Assert.False(await system.TestChildren.AnyAsync(row => row.Id == child, cancellationToken));
        Assert.False(await system.TestGrandchildren.AnyAsync(row => row.Id == grandchild, cancellationToken));
        Assert.False(await system.TestParentLines.AnyAsync(row => row.ParentId == parent, cancellationToken));

        var history = await HistoryAsync([parent, child, grandchild], cancellationToken);
        Assert.Equal(3, history.Count);
        var parentEntry = history.Single(entry => entry.EntityId == parent);
        Assert.All(history, entry => Assert.Equal(parentEntry.Id, entry.CorrelationId));
        Assert.Equal(
            [(nameof(TestChild), child, "test-child-delete"), (nameof(TestGrandchild), grandchild, "test-grandchild-delete"), (nameof(TestParent), parent, "test-parent-delete")],
            history.Select(entry => (entry.EntityType, entry.EntityId, entry.TransitionKey)).OrderBy(entry => entry.EntityType, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_grandchild_another_transition_holds_refuses_the_delete_and_everything_stays()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (parent, child, grandchild) = await AddFamilyAsync(scenario.A, cancellationToken);
        await using (var other = this.database.CreateSystemContext())
        {
            await other.TestGrandchildren.Where(row => row.Id == grandchild).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-delete", cancellationToken: cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("is being moved by another step", result.Error, StringComparison.Ordinal);
        await AssertFamilyStaysAsync(parent, child, grandchild, grandchildHeld: true, cancellationToken);
    }

    [Fact]
    public async Task A_childs_condition_refuses_the_delete_and_rolls_everything_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (parent, child, grandchild) = await AddFamilyAsync(scenario.A, cancellationToken, pinned: true);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();
        var offered = (await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(parent, cancellationToken: cancellationToken)).Value!
            .Single(answer => answer.Transition.Key == "test-parent-delete");
        var result = await service.TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-delete", cancellationToken: cancellationToken);

        Assert.Equal(["The child is not pinned"], offered.BlockingReasons);
        Assert.False(result.IsSuccess);
        Assert.Equal("The child is not pinned", result.Error);
        await AssertFamilyStaysAsync(parent, child, grandchild, grandchildHeld: false, cancellationToken);
    }

    [Fact]
    public async Task An_AsNoTracking_configurator_over_the_includes_still_removes_the_whole_family()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (parent, child, grandchild) = await AddFamilyAsync(scenario.A, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(
            parent,
            "test-parent-delete",
            query => query.AsNoTracking().Include(row => row.Children).ThenInclude(row => row.Grandchildren),
            cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.AnyAsync(row => row.Id == parent, cancellationToken));
        Assert.False(await system.TestChildren.AnyAsync(row => row.Id == child, cancellationToken));
        Assert.False(await system.TestGrandchildren.AnyAsync(row => row.Id == grandchild, cancellationToken));
        Assert.Equal(3, (await HistoryAsync([parent, child, grandchild], cancellationToken)).Count);
    }

    [Fact]
    public async Task A_child_left_in_its_delete_state_is_removed_with_the_parent_without_a_history_row_of_its_own()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (parent, child, grandchild) = await AddFamilyAsync(scenario.A, cancellationToken);

        Guid leftBehind;
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            using var lease = writer.BeginSystemScope("test: insert a child left behind in its delete state");
            var row = new TestChild { ParentId = parent, Name = "Left behind", State = TestChildState.Deleted };
            writer.TestChildren.Add(row);
            await writer.SaveChangesAsync(cancellationToken);
            leftBehind = row.Id;
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-delete", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.AnyAsync(row => row.Id == parent, cancellationToken));
        Assert.False(await system.TestChildren.AnyAsync(row => row.ParentId == parent, cancellationToken));
        var history = await HistoryAsync([parent, child, grandchild, leftBehind], cancellationToken);
        Assert.Equal(3, history.Count);
        Assert.Equal(new HashSet<Guid> { parent, child, grandchild }, history.Select(entry => entry.EntityId).ToHashSet());
        Assert.Contains(host.CommandLog, command => LockStatement().IsMatch(command) && command.Contains("[TestChildren]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_child_left_in_its_delete_state_goes_with_everything_below_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (parent, child, grandchild) = await AddFamilyAsync(scenario.A, cancellationToken);

        Guid leftBehind, openBelow, deletedBelow;
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            using var lease = writer.BeginSystemScope("test: insert a child left behind in its delete state, with its own children");
            var open = new TestGrandchild { Name = "Open below" };
            var deleted = new TestGrandchild { Name = "Deleted below", State = TestGrandchildState.Deleted };
            var row = new TestChild { ParentId = parent, Name = "Left behind", State = TestChildState.Deleted, Grandchildren = [open, deleted] };
            writer.TestChildren.Add(row);
            await writer.SaveChangesAsync(cancellationToken);
            (leftBehind, openBelow, deletedBelow) = (row.Id, open.Id, deleted.Id);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-delete", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.AnyAsync(row => row.Id == parent, cancellationToken));
        Assert.False(await system.TestChildren.AnyAsync(row => row.ParentId == parent, cancellationToken));
        Assert.False(await system.TestGrandchildren.AnyAsync(row => row.ChildId == leftBehind || row.Id == grandchild, cancellationToken));
        var history = await HistoryAsync([parent, child, grandchild, leftBehind, openBelow, deletedBelow], cancellationToken);
        Assert.Equal(new HashSet<Guid> { parent, child, grandchild, openBelow }, history.Select(entry => entry.EntityId).ToHashSet());
        var parentEntry = history.Single(entry => entry.EntityId == parent);
        Assert.All(history, entry => Assert.Equal(parentEntry.Id, entry.CorrelationId));
        Assert.Equal("test-grandchild-delete", history.Single(entry => entry.EntityId == openBelow).TransitionKey);
    }

    [Fact]
    public async Task A_grandchilds_action_failing_inside_the_transaction_rolls_everything_back_and_dispatches_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var (parent, child, grandchild) = await AddFamilyAsync(scenario.A, cancellationToken, grandchildName: TestGrandchild.FailWhenDeleted);
        var probe = new CommitProbe(this.database);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<IOutgoingMessageDispatcher, ProbingDispatcher>();
        });
        var service = host.Get<WorkflowService>();
        var offered = (await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(parent, cancellationToken: cancellationToken)).Value!
            .Single(answer => answer.Transition.Key == "test-parent-delete");
        var result = await service.TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-delete", cancellationToken: cancellationToken);

        Assert.True(offered.CanRun, string.Join(" ", offered.BlockingReasons));
        Assert.Equal(3, host.CommandLog.Count(command => LockStatement().IsMatch(command)));
        Assert.Equal((false, "The step 'Deleted' could not be completed, so nothing was changed."), (result.IsSuccess, result.Error));
        Assert.Empty(((ProbingDispatcher)host.Get<IOutgoingMessageDispatcher>()).Batches);
        Assert.Empty(probe.Records);
        await AssertFamilyStaysAsync(parent, child, grandchild, grandchildHeld: false, cancellationToken, grandchildName: TestGrandchild.FailWhenDeleted);
    }

    private async Task<(Guid Parent, Guid Child, Guid Grandchild)> AddFamilyAsync(ScenarioTenant tenant, CancellationToken cancellationToken, bool pinned = false, string grandchildName = "Grandchild")
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        var grandchild = new TestGrandchild { Name = grandchildName };
        var child = new TestChild { Name = "Child", IsPinned = pinned, Grandchildren = [grandchild] };
        var parent = new TestParent { Name = "Parent", Children = [child], Lines = [new TestParentLine { Code = "L1", Text = "Line" }] };
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return (parent.Id, child.Id, grandchild.Id);
    }

    private async Task AssertFamilyStaysAsync(Guid parent, Guid child, Guid grandchild, bool grandchildHeld, CancellationToken cancellationToken, string grandchildName = "Grandchild")
    {
        await using var system = this.database.CreateSystemContext();
        var storedParent = await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == parent, cancellationToken);
        var storedChild = await system.TestChildren.AsNoTracking().SingleAsync(row => row.Id == child, cancellationToken);
        var storedGrandchild = await system.TestGrandchildren.AsNoTracking().SingleAsync(row => row.Id == grandchild, cancellationToken);
        Assert.Equal((TestParentState.Draft, false), (storedParent.State, storedParent.IsInTransition));
        Assert.Equal((TestChildState.Draft, false), (storedChild.State, storedChild.IsInTransition));
        Assert.Equal((TestGrandchildState.Open, grandchildHeld, grandchildName), (storedGrandchild.State, storedGrandchild.IsInTransition, storedGrandchild.Name));
        Assert.True(await system.TestParentLines.AnyAsync(row => row.ParentId == parent, cancellationToken));
        Assert.Empty(await HistoryAsync([parent, child, grandchild], cancellationToken));
    }

    private async Task<List<StateHistoryEntry>> HistoryAsync(Guid[] entityIds, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entityIds.Contains(entry.EntityId)).ToListAsync(cancellationToken);
    }

    [GeneratedRegex(@"^UPDATE \[\w+\]\s+SET \[\w+\]\.\[IsInTransition\] = ")]
    private static partial Regex LockStatement();
}
