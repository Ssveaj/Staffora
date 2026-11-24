using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class StateHistoryWriterTests : IClassFixture<RealDatabase>
{
    private static readonly DateTimeOffset noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public StateHistoryWriterTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_transition_executed_by_the_engine_on_TestParent_stores_one_entry_with_from_to_key_actor_and_correlation_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var actor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Coordinator", cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);
        var correlationId = Guid.NewGuid();

        await using (var context = this.database.CreateContext(scenario.A.RuntimeOf(actor), new FakeClock(noon)))
        {
            var parent = await context.TestParents.Include(row => row.Attributes).SingleAsync(row => row.Id == id, cancellationToken);
            var outcome = await parent.GetWorkflow<TestParentState, TestParent>()
                .ExecuteTransitionAsync(parent, "test-parent-activate", new WorkflowEnvironment(context.Clock, NavigationLoadState.From(context), customFields: TestCustomFields.NoParentFields), cancellationToken: cancellationToken);

            StateHistoryWriter.Add(context, outcome, correlationId);
            using (context.BeginTransitionSaveScope([parent], "test-parent-activate"))
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        var entry = Assert.Single(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal(nameof(TestParent), entry.EntityType);
        Assert.Equal((int)TestParentState.Draft, entry.FromState);
        Assert.Equal((int)TestParentState.Active, entry.ToState);
        Assert.Equal("test-parent-activate", entry.TransitionKey);
        Assert.Equal(actor, entry.ActorId);
        Assert.Equal(correlationId, entry.CorrelationId);
        Assert.Equal(scenario.A.TenantId, entry.TenantId);
        Assert.Equal(actor, entry.CreatedById);
        Assert.Equal(noon.UtcDateTime, entry.Created);
    }

    [Fact]
    public async Task Every_entity_a_cascade_moved_gets_its_own_entry_correlated_with_the_primary_entry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken, withChildAndGrandchild: true);
        IReadOnlyList<StateHistoryEntry> written;

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.Include(row => row.Children).ThenInclude(child => child.Grandchildren).SingleAsync(row => row.Id == id, cancellationToken);
            var outcome = await parent.GetWorkflow<TestParentState, TestParent>()
                .ExecuteTransitionAsync(parent, "test-parent-delete", new WorkflowEnvironment(context.Clock, NavigationLoadState.From(context)), cancellationToken: cancellationToken);

            written = StateHistoryWriter.Add(context, outcome);
            using (context.BeginTransitionSaveScope(outcome.Steps.Select(step => step.Entity), "test-parent-delete"))
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        var stored = await HistoryByCorrelationAsync(written[0].Id, cancellationToken);
        Assert.Equal(["test-parent-delete", "test-child-delete", "test-grandchild-delete"], written.Select(entry => entry.TransitionKey));
        Assert.Equal([nameof(TestChild), nameof(TestGrandchild), nameof(TestParent)], stored.Select(entry => entry.EntityType).Order());
        Assert.All(stored, entry => Assert.Equal(written[0].Id, entry.CorrelationId));
        Assert.Equal(id, stored.Single(entry => entry.EntityType == nameof(TestParent)).EntityId);
    }

    [Fact]
    public async Task An_entry_has_no_foreign_key_to_its_entity_and_outlives_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.Include(row => row.Children).SingleAsync(row => row.Id == id, cancellationToken);
            var outcome = await parent.GetWorkflow<TestParentState, TestParent>()
                .ExecuteTransitionAsync(parent, "test-parent-delete", new WorkflowEnvironment(context.Clock, NavigationLoadState.From(context)), cancellationToken: cancellationToken);
            StateHistoryWriter.Add(context, outcome);

            context.TestParents.Remove(parent);
            using (context.BeginTransitionSaveScope([parent], "test-parent-delete"))
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        Assert.Equal((int)TestParentState.Deleted, Assert.Single(await HistoryOfAsync(id, cancellationToken)).ToState);
        await using var reader = this.database.CreateSystemContext();
        Assert.False(await reader.TestParents.AnyAsync(row => row.Id == id, cancellationToken));
    }

    [Fact]
    public async Task Another_tenants_context_reads_none_of_the_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await context.TestParents.Include(row => row.Attributes).SingleAsync(row => row.Id == id, cancellationToken);
            var outcome = await parent.GetWorkflow<TestParentState, TestParent>()
                .ExecuteTransitionAsync(parent, "test-parent-save", new WorkflowEnvironment(context.Clock, NavigationLoadState.From(context)), cancellationToken: cancellationToken);
            StateHistoryWriter.Add(context, outcome);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var readerA = this.database.CreateContext(scenario.A.Runtime());
        await using var readerB = this.database.CreateContext(scenario.B.Runtime());
        Assert.Single(await readerA.StateHistoryEntries.Where(entry => entry.EntityId == id).ToListAsync(cancellationToken));
        Assert.Empty(await readerB.StateHistoryEntries.Where(entry => entry.EntityId == id).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task An_entry_is_never_edited_and_is_deleted_only_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);
        await ActivateAsync(scenario.A, id, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var entry = await context.StateHistoryEntries.SingleAsync(row => row.EntityId == id, cancellationToken);
            entry.ToState = (int)TestParentState.Canceled;
            entry.TransitionKey = "forged";
            var edit = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Equal([nameof(StateHistoryEntry.ToState), nameof(StateHistoryEntry.TransitionKey)], edit.Fields.Order(StringComparer.Ordinal));
            Assert.Contains("never edited", edit.Message);
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Remove(await context.StateHistoryEntries.SingleAsync(row => row.EntityId == id, cancellationToken));
            var delete = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Contains("deleted only in system scope", delete.Message);
        }

        await using (var system = this.database.CreateContext(scenario.A.Runtime()))
        using (system.BeginSystemScope("test: edit history in system scope"))
        {
            (await system.StateHistoryEntries.SingleAsync(row => row.EntityId == id, cancellationToken)).TransitionKey = "forged";
            var edit = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Contains("never edited", edit.Message);
        }

        var stored = Assert.Single(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal(("test-parent-activate", (int)TestParentState.Active), (stored.TransitionKey, stored.ToState));

        await using (var system = this.database.CreateSystemContext())
        {
            system.Remove(await system.StateHistoryEntries.SingleAsync(row => row.EntityId == id, cancellationToken));
            await system.SaveChangesAsync(cancellationToken);
        }

        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_platform_owners_history_entry_on_Tenant_A_is_stamped_with_Tenant_As_system_account_and_the_owner_as_ActorId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var context = this.database.CreateContext(platform.Runtime(), new FakeClock(noon)))
        using (context.BeginSystemScope("test: platform owner suspends a tenant"))
        {
            var tenant = await context.Tenants.SingleAsync(row => row.Id == scenario.A.TenantId, cancellationToken);

            using var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddScoped<ITenantSignOut, TenantSignOut>().BuildServiceProvider();
            var outcome = await tenant.GetWorkflow<TenantState, Tenant>()
                .ExecuteTransitionAsync(tenant, "tenant-suspend", new WorkflowEnvironment(context.Clock, NavigationLoadState.From(context), services), cancellationToken: cancellationToken);
            StateHistoryWriter.Add(context, outcome);

            using (context.BeginTransitionSaveScope([tenant], "tenant-suspend"))
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        var entry = Assert.Single(await HistoryOfAsync(scenario.A.TenantId, cancellationToken));
        Assert.Equal(("tenant-suspend", (int)TenantState.Active, (int)TenantState.Suspended), (entry.TransitionKey, entry.FromState, entry.ToState));
        Assert.Equal(scenario.A.TenantId, entry.TenantId);
        Assert.Equal(platform.OwnerId, entry.ActorId);
        Assert.Equal(scenario.A.SystemAccountId, entry.CreatedById);
        Assert.Null(entry.ImpersonatedById);
        Assert.Equal(noon.UtcDateTime, entry.Created);
    }

    [Fact]
    public async Task A_tenant_users_history_entry_in_their_own_tenant_is_stamped_with_that_user_not_the_system_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        using (context.BeginSystemScope("test: a tenant user's transition in system scope"))
        {
            var parent = await context.TestParents.Include(row => row.Attributes).SingleAsync(row => row.Id == id, cancellationToken);
            var outcome = await parent.GetWorkflow<TestParentState, TestParent>()
                .ExecuteTransitionAsync(parent, "test-parent-activate", new WorkflowEnvironment(context.Clock, NavigationLoadState.From(context), customFields: TestCustomFields.NoParentFields), cancellationToken: cancellationToken);
            StateHistoryWriter.Add(context, outcome);
            using (context.BeginTransitionSaveScope([parent], "test-parent-activate"))
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        var entry = Assert.Single(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal((scenario.A.OwnerId, scenario.A.OwnerId), (entry.ActorId, entry.CreatedById));
    }

    private async Task ActivateAsync(ScenarioTenant tenant, Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateContext(tenant.Runtime());
        var parent = await context.TestParents.Include(row => row.Attributes).SingleAsync(row => row.Id == id, cancellationToken);
        var outcome = await parent.GetWorkflow<TestParentState, TestParent>()
            .ExecuteTransitionAsync(parent, "test-parent-activate", new WorkflowEnvironment(context.Clock, NavigationLoadState.From(context), customFields: TestCustomFields.NoParentFields), cancellationToken: cancellationToken);
        StateHistoryWriter.Add(context, outcome);
        using (context.BeginTransitionSaveScope([parent], "test-parent-activate"))
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, CancellationToken cancellationToken, bool withChildAndGrandchild = false)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        var parent = new TestParent { Name = "Parent" };
        if (withChildAndGrandchild)
        {
            var child = new TestChild { Name = "Child" };
            child.Grandchildren.Add(new TestGrandchild { Name = "Grandchild" });
            parent.Children.Add(child);
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<List<StateHistoryEntry>> HistoryOfAsync(Guid entityId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == entityId).ToListAsync(cancellationToken);
    }

    private async Task<List<StateHistoryEntry>> HistoryByCorrelationAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.CorrelationId == correlationId).ToListAsync(cancellationToken);
    }
}
