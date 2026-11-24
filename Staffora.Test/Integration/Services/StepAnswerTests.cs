using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class StepAnswerTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public StepAnswerTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_user_who_may_not_open_the_entity_is_refused_its_history_for_own_foreign_and_missing_ids_alike()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var ours = await AddParentAsync(scenario.A, cancellationToken);
        var theirs = await AddParentAsync(scenario.B, cancellationToken);
        var ids = new[] { ours, theirs, Guid.NewGuid() };

        await using var without = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = without.Get<TestParentDialogService>();
        foreach (var id in ids)
        {
            without.ClearCommandLog();
            var history = await service.GetTestParentHistoryAsync(id, StateHistoryQuery.Newest(3), cancellationToken);
            Assert.Equal((false, TestParentDialogService.NoAccess), (history.IsSuccess, history.Error));
            Assert.DoesNotContain(without.CommandLog, command => command.Contains("[TestParents]", StringComparison.Ordinal) || command.Contains("[StateHistoryEntries]", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_cascades_reason_blocks_a_step_even_in_a_conditions_words()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        var parent = await AddParentAsync(scenario.A, cancellationToken, new TestChild { Name = "Pinned", IsPinned = true });

        var child = await AddChildAsync(scenario.A, parent, cancellationToken, new TestGrandchild { Name = TestGrandchild.HoldsItsChildOpen });

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var workflows = host.Get<WorkflowService>();

        var parentAnswers = (await workflows.GetAvailableTransitionsAsync<TestParentState, TestParent>(parent, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken)).Value!;
        var delete = parentAnswers.Single(answer => answer.Transition.Key == "test-parent-delete");
        Assert.Empty(delete.Transition.Conditions);
        Assert.Equal(["The child is not pinned"], delete.BlockingReasons);
        Assert.False(delete.CanRun);

        var childAnswers = (await workflows.GetAvailableTransitionsAsync<TestChildState, TestChild>(child, TestChildWorkflowQueries.WithWorkflowGraph, cancellationToken)).Value!;
        var cancel = childAnswers.Single(answer => answer.Transition.Key == "test-child-cancel-draft");
        Assert.Equal([TestGrandchild.NothingHoldsTheChildOpen, TestGrandchild.NothingHoldsTheChildOpen], cancel.BlockingReasons);
    }

    [Fact]
    public async Task A_row_another_step_holds_blocks_every_step_with_the_hold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var parent = await AddParentAsync(scenario.A, cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestParents.Where(row => row.Id == parent).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var answers = (await host.Get<WorkflowService>().GetAvailableTransitionsAsync<TestParentState, TestParent>(parent, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken)).Value!;

        var save = answers.Single(answer => answer.Transition.Key == "test-parent-save");
        var held = Assert.Single(save.BlockingReasons);
        Assert.Contains("is being moved by another step right now", held, StringComparison.Ordinal);
        Assert.All(answers, answer => Assert.False(answer.CanRun));
    }

    [Fact]
    public async Task A_system_driven_step_never_reads_as_one_the_user_can_take()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var parent = await AddParentAsync(scenario.A, cancellationToken, state: TestParentState.Canceled);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var answers = (await host.Get<WorkflowService>().GetAvailableTransitionsAsync<TestParentState, TestParent>(parent, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken)).Value!;

        var restore = answers.Single(answer => answer.Transition.Key == "test-parent-restore");
        Assert.True(restore.Transition.IsSystemDriven);
        Assert.Empty(restore.BlockingReasons);
        Assert.Contains("is taken by the system alone", restore.AccessRefusal, StringComparison.Ordinal);
        Assert.False(restore.CanRun);
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, CancellationToken cancellationToken, TestChild? child = null, TestParentState state = TestParentState.Draft)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", State = state };
        if (child is not null)
        {
            parent.Children.Add(child);
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<Guid> AddChildAsync(ScenarioTenant tenant, Guid parent, CancellationToken cancellationToken, TestGrandchild grandchild)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a child as a transition left it");
        var child = new TestChild { ParentId = parent, Name = "Child" };
        child.Grandchildren.Add(grandchild);
        writer.TestChildren.Add(child);
        await writer.SaveChangesAsync(cancellationToken);
        return child.Id;
    }
}
