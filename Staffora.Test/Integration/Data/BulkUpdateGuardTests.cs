using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class BulkUpdateGuardTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public BulkUpdateGuardTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Moving_a_row_to_another_tenant_with_ExecuteUpdate_is_refused_in_tenant_and_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);
        var before = await LoadAsync(id, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.TestParents.Where(row => row.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.TenantId, scenario.B.TenantId), cancellationToken));

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.TestParents.Where(row => row.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => EF.Property<Guid>(row, "TenantId"), scenario.B.TenantId), cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await Assert.ThrowsAsync<TenantIsolationException>(() => system.TestParents.Where(row => row.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.TenantId, scenario.B.TenantId), cancellationToken));
        }

        await using var readerB = this.database.CreateContext(scenario.B.Runtime());
        Assert.False(await readerB.TestParents.AnyAsync(row => row.Id == id, cancellationToken));
        AssertUnchanged(before, await LoadAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Forging_the_creation_stamp_with_ExecuteUpdate_is_refused_in_tenant_and_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);
        var before = await LoadAsync(id, cancellationToken);
        var longAgo = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var rows = writerA.TestParents.Where(row => row.Id == id);
            await Assert.ThrowsAsync<TenantIsolationException>(() => rows.ExecuteUpdateAsync(set => set.SetProperty(row => row.CreatedById, scenario.B.OwnerId), cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => rows.ExecuteUpdateAsync(set => set.SetProperty(row => row.ImpersonatedById, scenario.B.OwnerId), cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => rows.ExecuteUpdateAsync(set => set.SetProperty(row => row.Created, longAgo), cancellationToken));

            await Assert.ThrowsAsync<TenantIsolationException>(() => rows.ExecuteUpdateAsync(
                set => set.SetProperty(row => row.Notes, "Forged").SetProperty(row => row.CreatedById, scenario.B.OwnerId), cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await Assert.ThrowsAsync<TenantIsolationException>(() => system.TestParents.Where(row => row.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.CreatedById, scenario.B.OwnerId), cancellationToken));
        }

        AssertUnchanged(before, await LoadAsync(id, cancellationToken));
    }

    [Fact]
    public async Task An_ExecuteUpdate_of_other_columns_still_runs_and_stays_inside_the_runtime_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var mine = await AddParentAsync(scenario.A, cancellationToken);
        var theirs = await AddParentAsync(scenario.B, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var updated = await writerA.TestParents.ExecuteUpdateAsync(set => set.SetProperty(row => row.Notes, row => row.Name), cancellationToken);
            Assert.Equal(1, updated);
        }

        Assert.Equal("Parent", (await LoadAsync(mine, cancellationToken)).Notes);
        Assert.Null((await LoadAsync(theirs, cancellationToken)).Notes);
    }

    [Fact]
    public async Task State_history_is_never_rewritten_or_removed_by_a_bulk_statement_in_tenant_or_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);
        await ActivateAsync(scenario.A, id, cancellationToken);
        var before = await HistoryOfAsync(id, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var entries = writerA.StateHistoryEntries.Where(entry => entry.EntityId == id);
            var update = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => entries.ExecuteUpdateAsync(
                set => set.SetProperty(entry => entry.TransitionKey, "forged").SetProperty(entry => entry.ToState, (int)TestParentState.Deleted), cancellationToken));
            Assert.Contains("state history", update.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => entries.ExecuteDeleteAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var entries = system.StateHistoryEntries.Where(entry => entry.EntityId == id && entry.TenantId == scenario.A.TenantId);
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => entries.ExecuteUpdateAsync(set => set.SetProperty(entry => entry.TransitionKey, "forged"), cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => entries.ExecuteDeleteAsync(cancellationToken));
        }

        var after = Assert.Single(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal((before.Single().TransitionKey, before.Single().ToState), (after.TransitionKey, after.ToState));
    }

    [Fact]
    public async Task A_definition_takes_its_transition_lock_as_its_one_bulk_update()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var field = new Staffora.Core.Entities.EntityAttributes.UserAttributes.ApplicationUserAttributeDefinition { Name = "Locker", ValueType = Staffora.Core.Entities.EntityAttributes.AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId, State = Staffora.Core.Entities.EntityAttributes.CustomFieldState.Active };
        await using (var system = this.database.CreateSystemContext())
        {
            system.Add(field);
            await system.SaveChangesAsync(cancellationToken);
        }

        await using (var system = this.database.CreateSystemContext())
        using (system.BeginSystemScope("test: lock a custom field as a step does"))
        {
            var rows = system.ApplicationUserAttributeDefinitions.Where(row => row.Id == field.Id && !row.IsInTransition);
            Assert.Equal(1, await rows.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken));
            var held = system.ApplicationUserAttributeDefinitions.Where(row => row.Id == field.Id);
            await Assert.ThrowsAsync<TenantIsolationException>(() => held.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, false).SetProperty(row => row.Name, "Bulk"), cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => held.ExecuteDeleteAsync(cancellationToken));
            Assert.Equal(1, await held.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, false), cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.ApplicationUserAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Id == field.Id, cancellationToken);
        Assert.Equal(("Locker", false), (stored.Name, stored.IsInTransition));
    }

    [Fact]
    public async Task A_state_entity_is_never_deleted_by_a_bulk_statement_while_a_row_without_a_lifecycle_may_be()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken, withLineAndChild: true);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            Assert.Equal(1, await writerA.TestParentLines.Where(line => line.ParentId == id).ExecuteDeleteAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestParents.Where(row => row.Id == id && row.TenantId == scenario.A.TenantId).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken);
        }

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writerA.TestParents.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken));
            Assert.Contains("deleted only through its workflow", refusal.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writerA.TestChildren.Where(row => row.ParentId == id).ExecuteDeleteAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.TestParents.Where(row => row.Id == id && row.TenantId == scenario.A.TenantId).ExecuteDeleteAsync(cancellationToken));
        }

        await using var reader = this.database.CreateSystemContext();
        Assert.True(await reader.TestParents.AnyAsync(row => row.Id == id, cancellationToken));
        Assert.Equal(1, await reader.TestChildren.CountAsync(row => row.ParentId == id, cancellationToken));
    }

    private static void AssertUnchanged(TestParent before, TestParent after)
    {
        Assert.Equal(before.TenantId, after.TenantId);
        Assert.Equal(before.Created, after.Created);
        Assert.Equal(before.CreatedById, after.CreatedById);
        Assert.Equal(before.ImpersonatedById, after.ImpersonatedById);
        Assert.Equal(before.Notes, after.Notes);
    }

    [Fact]
    public async Task The_shape_of_an_organization_tree_is_never_changed_by_a_bulk_update_in_tenant_or_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        Guid north, south;
        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var northRow = new OrganizationUnit { Name = "North", UnitType = OrganizationUnitType.Region, ParentId = rootId };
            var southRow = new OrganizationUnit { Name = "South", UnitType = OrganizationUnitType.Region, ParentId = rootId };
            var role = new TenantRole { Name = "Coordinator" };
            writerA.AddRange(northRow, southRow, role);
            writerA.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { UserId = scenario.A.OwnerId, TenantRole = role, OrganizationUnitId = rootId });
            await writerA.SaveChangesAsync(cancellationToken);
            (north, south) = (northRow.Id, southRow.Id);
        }

        async Task RefusedAsync(ApplicationDbContext context)
        {
            var units = context.OrganizationUnits.Where(unit => unit.Id == north);
            var parent = await Assert.ThrowsAsync<OrganizationStructureException>(() => units.ExecuteUpdateAsync(set => set.SetProperty(unit => unit.ParentId, south), cancellationToken));
            Assert.Equal(ApplicationDbContext.BulkStructureWriteReason(nameof(OrganizationUnit), nameof(OrganizationUnit.ParentId)), parent.Message);
            await Assert.ThrowsAsync<OrganizationStructureException>(() => units.ExecuteUpdateAsync(set => set.SetProperty(unit => unit.UnitType, OrganizationUnitType.Grouping), cancellationToken));

            var state = await Assert.ThrowsAsync<OrganizationStructureException>(() => units.ExecuteUpdateAsync(set => set.SetProperty(unit => unit.State, OrganizationUnitState.Archived), cancellationToken));
            Assert.Equal(ApplicationDbContext.BulkStructureWriteReason(nameof(OrganizationUnit), nameof(OrganizationUnit.State)), state.Message);
            await Assert.ThrowsAsync<OrganizationStructureException>(() => units.ExecuteUpdateAsync(set => set.SetProperty(unit => EF.Property<Guid?>(unit, "ParentId"), south), cancellationToken));
            await Assert.ThrowsAsync<OrganizationStructureException>(() => context.OrganizationUnitUserRoles.ExecuteUpdateAsync(set => set.SetProperty(holding => holding.OrganizationUnitId, north), cancellationToken));
        }

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            await RefusedAsync(writerA);

            Assert.Equal(1, await writerA.OrganizationUnits.Where(unit => unit.Id == north).ExecuteUpdateAsync(set => set.SetProperty(unit => unit.Description, "Bulk"), cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await RefusedAsync(system);
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.OrganizationUnits.SingleAsync(unit => unit.Id == north, cancellationToken);
        Assert.Equal((rootId, OrganizationUnitType.Region, "Bulk", OrganizationUnitState.Create), (stored.ParentId, stored.UnitType, stored.Description, stored.State));
        Assert.Equal(rootId, await check.OrganizationUnitUserRoles.Where(holding => holding.TenantId == scenario.A.TenantId && holding.UserId == scenario.A.OwnerId && holding.TenantRole.Name == "Coordinator").Select(holding => holding.OrganizationUnitId).SingleAsync(cancellationToken));
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, CancellationToken cancellationToken, bool withLineAndChild = false)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        var parent = new TestParent { Name = "Parent" };
        if (withLineAndChild)
        {
            parent.Lines.Add(new TestParentLine { Code = "L1", Text = "Line" });
            parent.Children.Add(new TestChild { Name = "Child" });
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<TestParent> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
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

    private async Task<List<StateHistoryEntry>> HistoryOfAsync(Guid entityId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == entityId).ToListAsync(cancellationToken);
    }
}
