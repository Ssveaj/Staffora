using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class OrganizationUnitLifecycleTests : IClassFixture<RealDatabase>
{
    private static readonly RolePermission[] unitAdministrator =
        [RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.OrganizationUnitsDelete, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView];

    private readonly RealDatabase database;

    public OrganizationUnitLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Deleting_a_leaf_unit_removes_it_and_keeps_its_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var leaf = await units.CreateUnitIdAsync("Leaf office", OrganizationUnitType.Branch, rootId, cancellationToken);

        var deleted = await units.DeleteAsync(leaf, cancellationToken);

        Assert.True(deleted.IsSuccess, deleted.Error);
        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.OrganizationUnits.AnyAsync(unit => unit.Id == leaf, cancellationToken));
        var history = await check.StateHistoryEntries.Where(entry => entry.EntityId == leaf).OrderBy(entry => entry.Created).Select(entry => new { entry.TransitionKey, entry.FromState, entry.ToState }).ToListAsync(cancellationToken);
        Assert.Equal(
            [(OrganizationUnitWorkflowAttribute.CreateStep, (int)OrganizationUnitState.Create, (int)OrganizationUnitState.Active), (OrganizationUnitWorkflowAttribute.DeleteStep, (int)OrganizationUnitState.Active, (int)OrganizationUnitState.Deleted)],
            history.Select(entry => (entry.TransitionKey, entry.FromState, entry.ToState)));

        Assert.Equal(2, await check.StateHistoryEntries.CountAsync(entry => entry.EntityId == leaf && entry.ActorId == scenario.A.OwnerId, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Deleting_a_unit_with_units_below_or_the_root_is_refused_with_the_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var region = await units.CreateUnitIdAsync("North", OrganizationUnitType.Region, rootId, cancellationToken);
        var branch = await units.CreateUnitIdAsync("Harbour", OrganizationUnitType.Branch, region, cancellationToken);

        Assert.Equal(OrganizationUnitWorkflowAttribute.NoUnitsBelowReason, (await units.DeleteAsync(region, cancellationToken)).Error);
        Assert.Contains(OrganizationStructurePolicy.RootIsNeverDeleted, (await units.DeleteAsync(rootId, cancellationToken)).Error, StringComparison.Ordinal);

        var workflows = host.Get<WorkflowService>();
        Assert.Contains(OrganizationUnitWorkflowAttribute.NoUnitsBelowReason, await BlockingReasonsAsync(workflows, region, OrganizationUnitWorkflowAttribute.DeleteStep, cancellationToken));
        Assert.Contains(OrganizationStructurePolicy.RootIsNeverDeleted, await BlockingReasonsAsync(workflows, rootId, OrganizationUnitWorkflowAttribute.DeleteStep, cancellationToken));

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.Equal(3, await check.OrganizationUnits.CountAsync(unit => unit.Id == rootId || unit.Id == region || unit.Id == branch, cancellationToken));
        }

        Assert.True((await units.DeleteAsync(branch, cancellationToken)).IsSuccess);
        Assert.True((await units.DeleteAsync(region, cancellationToken)).IsSuccess);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_registered_blocker_refuses_a_unit_delete_with_its_reason_inside_the_step_and_under_the_structure_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        Guid used, free;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            used = await owner.Get<OrganizationService>().CreateUnitIdAsync("Used office", OrganizationUnitType.Branch, rootId, cancellationToken);
            free = await owner.Get<OrganizationService>().CreateUnitIdAsync("Free office", OrganizationUnitType.Branch, rootId, cancellationToken);
        }

        var blocker = new TestUnitBlocker(used);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton<IDeleteBlocker>(blocker));
        var units = host.Get<OrganizationService>();

        Assert.Contains(TestUnitBlocker.Reason, await BlockingReasonsAsync(host.Get<WorkflowService>(), used, OrganizationUnitWorkflowAttribute.DeleteStep, cancellationToken));

        blocker.Calls.Clear();
        Assert.Equal(TestUnitBlocker.Reason, (await units.DeleteAsync(used, cancellationToken)).Error);
        Assert.Equal([(true, "Exclusive")], blocker.Calls);
        await using (var check = this.database.CreateSystemContext())
        {
            var stored = await check.OrganizationUnits.AsNoTracking().SingleAsync(unit => unit.Id == used, cancellationToken);
            Assert.Equal((OrganizationUnitState.Active, false), (stored.State, stored.IsInTransition));
            Assert.False(await check.StateHistoryEntries.AnyAsync(entry => entry.EntityId == used && entry.TransitionKey == OrganizationUnitWorkflowAttribute.DeleteStep, cancellationToken));
        }

        Assert.True((await units.DeleteAsync(free, cancellationToken)).IsSuccess);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Role_holdings_on_a_unit_keep_it_from_being_deleted_until_they_are_removed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken, RolePermission.UsersView);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var tenants = host.Get<RoleHoldingService>();
        var office = await units.CreateUnitIdAsync("Staffed office", OrganizationUnitType.Branch, rootId, cancellationToken);
        var holding = await tenants.AssignUserToRoleAsync(colleague, office, role, scenario.A.TenantId);
        Assert.True(holding.IsSuccess, holding.Error);

        const string reason = "Roles are still held on this unit: remove the role holdings first, or archive the unit instead.";
        Assert.Equal(reason, RoleHoldingsBlockUnitDelete.Reason);
        Assert.Equal(reason, (await units.DeleteAsync(office, cancellationToken)).Error);
        var second = await tenants.AssignUserToRoleAsync(scenario.A.OwnerId, office, role, scenario.A.TenantId);
        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(reason, (await units.DeleteAsync(office, cancellationToken)).Error);

        Assert.True((await tenants.RemoveUserFromRoleAsync(holding.Value!.Id)).IsSuccess);
        Assert.True((await tenants.RemoveUserFromRoleAsync(second.Value!.Id)).IsSuccess);
        Assert.True((await units.DeleteAsync(office, cancellationToken)).IsSuccess);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Archiving_an_inactive_unit_keeps_to_no_ceiling_since_its_holdings_grant_nothing_already()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "UnitEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsEdit, RolePermission.RolesView);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        var strong = await AddRoleAsync(scenario.A, "Strong", cancellationToken, RolePermission.SettingsEdit);
        Guid dock;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            dock = await owner.Get<OrganizationService>().CreateUnitIdAsync("Dock", OrganizationUnitType.Branch, rootId, cancellationToken);
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(holder, dock, strong, scenario.A.TenantId)).IsSuccess);
            Assert.True((await owner.Get<OrganizationService>().StepAsync(dock, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor));
        var units = host.Get<OrganizationService>();

        Assert.Equal(OrganizationUnitWorkflowAttribute.WithinCeilingReason, (await units.StepAsync(dock, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).Error);
        var archived = await units.StepAsync(dock, OrganizationUnitWorkflowAttribute.ArchiveInactiveStep, cancellationToken);
        Assert.True(archived.IsSuccess, archived.Error);
        Assert.Equal(OrganizationUnitState.Archived, await StateOfAsync(dock, cancellationToken));
        Assert.False(await HoldsAsync(scenario.A, holder, RolePermission.SettingsEdit, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_unit_delete_racing_a_new_unit_or_holding_below_it_lets_exactly_one_through_with_a_rule_never_a_database_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var roleId = await AddRoleAsync(scenario.A, "Racer", cancellationToken, RolePermission.UsersView);
        for (var round = 0; round < 12; round++)
        {
            Guid unit;
            await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
            {
                unit = await owner.Get<OrganizationService>().CreateUnitIdAsync(TwoTenantScenario.UniqueName("Contested"), OrganizationUnitType.Region, rootId, cancellationToken);
            }

            await using var deleting = ServiceHost.Create(this.database, scenario.A.Runtime());
            await using var adding = ServiceHost.Create(this.database, scenario.A.Runtime());
            await deleting.Get<PermissionService>().HasPermissionAsync(RolePermission.OrganizationUnitsDelete, cancellationToken);
            await adding.Get<PermissionService>().HasPermissionAsync(RolePermission.OrganizationUnitsCreate, cancellationToken);
            var childRound = round % 2 == 0;
            var delete = Task.Run(() => deleting.Get<OrganizationService>().DeleteAsync(unit, cancellationToken), cancellationToken);
            var add = childRound
                ? Task.Run(async () => await adding.Get<OrganizationService>().CreateAsync(UnitSteps.Draft("Below", OrganizationUnitType.Branch, unit), cancellationToken) is var made ? (made.IsSuccess, made.Error) : default, cancellationToken)
                : Task.Run(async () => await adding.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, unit, roleId, scenario.A.TenantId) is var given ? (given.IsSuccess, given.Error) : default, cancellationToken);
            await Task.WhenAll(delete, add);
            var (deleted, added) = (await delete, await add);

            var outcome = $"round {round} ({(childRound ? "unit below" : "holding")}): delete {deleted.IsSuccess} '{deleted.Error}', add {added.IsSuccess} '{added.Error}'";
            Assert.True(deleted.IsSuccess ^ added.IsSuccess, outcome);
            Assert.DoesNotContain("An error occurred", deleted.Error + added.Error, StringComparison.Ordinal);
            await using var check = this.database.CreateSystemContext();
            var unitKept = await check.OrganizationUnits.AnyAsync(row => row.Id == unit, cancellationToken);
            var dependents = await check.OrganizationUnits.CountAsync(row => row.ParentId == unit, cancellationToken) + await check.OrganizationUnitUserRoles.CountAsync(row => row.OrganizationUnitId == unit, cancellationToken);
            Assert.Equal((!deleted.IsSuccess, added.IsSuccess ? 1 : 0), (unitKept, dependents));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_data_context_refuses_a_unit_taken_out_of_use_or_deleted_past_the_workflow_with_the_policys_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        Guid region, branch, paused, pausedBranch;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = owner.Get<OrganizationService>();
            region = await units.CreateUnitIdAsync("West", OrganizationUnitType.Region, rootId, cancellationToken);
            branch = await units.CreateUnitIdAsync("Ferry", OrganizationUnitType.Branch, region, cancellationToken);
            paused = await units.CreateUnitIdAsync("Paused", OrganizationUnitType.Region, rootId, cancellationToken);
            pausedBranch = await units.CreateUnitIdAsync("Paused branch", OrganizationUnitType.Branch, paused, cancellationToken);
            Assert.True((await units.StepAsync(paused, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);
        }

        Assert.Equal(OrganizationStructurePolicy.RootStaysActive, await RefusalOfStateAsync(rootId, OrganizationUnitState.Inactive, cancellationToken));
        Assert.Equal(OrganizationStructurePolicy.OutOfUseAboveInUseReason("West"), await RefusalOfStateAsync(region, OrganizationUnitState.Inactive, cancellationToken));
        Assert.Equal(OrganizationStructurePolicy.ArchivedAboveUnarchivedReason("Paused"), await RefusalOfStateAsync(paused, OrganizationUnitState.Archived, cancellationToken));

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = await system.OrganizationUnits.SingleAsync(unit => unit.Id == region, cancellationToken);
            using (system.BeginTransitionSaveScope([parent], "test: a delete past its condition"))
            {
                system.OrganizationUnits.Remove(parent);
                var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => system.SaveChangesAsync(cancellationToken));
                Assert.Equal(OrganizationStructurePolicy.UnitsBelowReason("West"), refused.Message);
            }
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(
            [OrganizationUnitState.Active, OrganizationUnitState.Active, OrganizationUnitState.Active, OrganizationUnitState.Inactive, OrganizationUnitState.Inactive],
            [await StateOfAsync(rootId, cancellationToken), await StateOfAsync(region, cancellationToken), await StateOfAsync(branch, cancellationToken), await StateOfAsync(paused, cancellationToken), await StateOfAsync(pausedBranch, cancellationToken)]);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_data_context_refuses_a_holding_written_or_moved_onto_a_unit_out_of_use_while_the_root_always_takes_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Planner", cancellationToken, RolePermission.UsersView);
        Guid paused, stored, open;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = owner.Get<OrganizationService>();
            paused = await units.CreateUnitIdAsync("Paused office", OrganizationUnitType.Branch, rootId, cancellationToken);
            Assert.True((await units.StepAsync(paused, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);
            stored = await units.CreateUnitIdAsync("Stored office", OrganizationUnitType.Branch, rootId, cancellationToken);
            Assert.True((await units.StepAsync(stored, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).IsSuccess);
            open = await units.CreateUnitIdAsync("Open office", OrganizationUnitType.Branch, rootId, cancellationToken);
        }

        Assert.Equal(OrganizationStructurePolicy.OutOfUseHoldsNoRoles("Paused office"), await RefusalOfHoldingAsync(scenario.A, colleague, role, paused, cancellationToken));
        Assert.Equal(OrganizationStructurePolicy.OutOfUseHoldsNoRoles("Stored office"), await RefusalOfHoldingAsync(scenario.A, colleague, role, stored, cancellationToken));
        Assert.Null(await RefusalOfHoldingAsync(scenario.A, colleague, role, open, cancellationToken));
        Assert.Null(await RefusalOfHoldingAsync(scenario.A, scenario.A.OwnerId, role, rootId, cancellationToken));

        await using (var system = this.database.CreateSystemContext())
        {
            var holding = await system.OrganizationUnitUserRoles.SingleAsync(row => row.UserId == colleague && row.OrganizationUnitId == open, cancellationToken);
            system.NameEditor(holding, holding.CreatedById);
            holding.OrganizationUnitId = paused;
            var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Equal(OrganizationStructurePolicy.OutOfUseHoldsNoRoles("Paused office"), refused.Message);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(0, await check.OrganizationUnitUserRoles.CountAsync(row => row.OrganizationUnitId == paused || row.OrganizationUnitId == stored, cancellationToken));
        Assert.Equal(1, await check.OrganizationUnitUserRoles.CountAsync(row => row.OrganizationUnitId == open, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Deactivating_takes_the_active_units_below_out_of_use_each_with_its_history_and_reactivation_goes_from_the_top()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var region = await units.CreateUnitIdAsync("North", OrganizationUnitType.Region, rootId, cancellationToken);
        var branch = await units.CreateUnitIdAsync("Harbour", OrganizationUnitType.Branch, region, cancellationToken);
        var team = await units.CreateUnitIdAsync("Night shift", OrganizationUnitType.Team, branch, cancellationToken);
        var closed = await units.CreateUnitIdAsync("Closed desk", OrganizationUnitType.Team, region, cancellationToken);
        Assert.True((await units.StepAsync(closed, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);

        var deactivated = await units.StepAsync(region, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken);

        Assert.True(deactivated.IsSuccess, deactivated.Error);
        Assert.Equal(
            [OrganizationUnitState.Inactive, OrganizationUnitState.Inactive, OrganizationUnitState.Inactive, OrganizationUnitState.Inactive],
            [await StateOfAsync(region, cancellationToken), await StateOfAsync(branch, cancellationToken), await StateOfAsync(team, cancellationToken), await StateOfAsync(closed, cancellationToken)]);

        await using (var check = this.database.CreateSystemContext())
        {
            var steps = await check.StateHistoryEntries.Where(entry => entry.TransitionKey == OrganizationUnitWorkflowAttribute.DeactivateStep && (entry.EntityId == region || entry.EntityId == branch || entry.EntityId == team || entry.EntityId == closed))
                .Select(entry => new { entry.EntityId, entry.CorrelationId }).ToListAsync(cancellationToken);
            var regionStep = steps.Single(step => step.EntityId == region);
            Assert.Equal(1, steps.Count(step => step.EntityId == branch && step.CorrelationId == regionStep.CorrelationId));
            Assert.Equal(1, steps.Count(step => step.EntityId == team && step.CorrelationId == regionStep.CorrelationId));
            Assert.Equal(1, steps.Count(step => step.EntityId == closed && step.CorrelationId != regionStep.CorrelationId));
        }

        Assert.Equal(OrganizationUnitWorkflowAttribute.ParentInUseReason, (await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).Error);
        Assert.True((await units.StepAsync(region, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).IsSuccess);
        Assert.Equal(OrganizationUnitState.Inactive, await StateOfAsync(branch, cancellationToken));
        Assert.True((await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).IsSuccess);
        Assert.Equal(OrganizationUnitState.Active, await StateOfAsync(branch, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Archiving_needs_every_unit_below_archived_and_restoring_needs_the_unit_above_out_of_the_archive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var region = await units.CreateUnitIdAsync("South", OrganizationUnitType.Region, rootId, cancellationToken);
        var branch = await units.CreateUnitIdAsync("Quay", OrganizationUnitType.Branch, region, cancellationToken);

        Assert.Equal(OrganizationUnitWorkflowAttribute.EveryUnitBelowArchivedReason, (await units.StepAsync(region, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).Error);
        Assert.True((await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).IsSuccess);
        Assert.True((await units.StepAsync(region, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).IsSuccess);
        Assert.Equal(OrganizationUnitWorkflowAttribute.RootIsNeverArchived, (await units.StepAsync(rootId, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).Error);

        Assert.False((await units.EditAsync(region, unit => unit.Name = "Renamed in the archive", cancellationToken)).IsSuccess);

        Assert.Equal(OrganizationUnitWorkflowAttribute.ParentNotArchivedReason, (await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.RestoreStep, cancellationToken)).Error);
        Assert.True((await units.StepAsync(region, OrganizationUnitWorkflowAttribute.RestoreStep, cancellationToken)).IsSuccess);
        Assert.Equal(OrganizationUnitState.Archived, await StateOfAsync(branch, cancellationToken));
        Assert.True((await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.RestoreStep, cancellationToken)).IsSuccess);
        Assert.Equal((OrganizationUnitState.Inactive, OrganizationUnitState.Inactive), (await StateOfAsync(region, cancellationToken), await StateOfAsync(branch, cancellationToken)));

        Assert.True((await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.ArchiveInactiveStep, cancellationToken)).IsSuccess);
        Assert.True((await units.DeleteAsync(branch, cancellationToken)).IsSuccess);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Deactivate_archive_and_delete_keep_to_the_grant_ceiling_over_the_holdings_they_take_out_of_use()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "UnitEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsEdit, RolePermission.OrganizationUnitsDelete, RolePermission.RolesView);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        var strong = await AddRoleAsync(scenario.A, "Strong", cancellationToken, RolePermission.SettingsEdit);
        Guid region, branch;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            region = await owner.Get<OrganizationService>().CreateUnitIdAsync("East", OrganizationUnitType.Region, rootId, cancellationToken);
            branch = await owner.Get<OrganizationService>().CreateUnitIdAsync("Pier", OrganizationUnitType.Branch, region, cancellationToken);
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(holder, branch, strong, scenario.A.TenantId)).IsSuccess);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor));
        var units = host.Get<OrganizationService>();

        Assert.Contains(OrganizationUnitWorkflowAttribute.WithinCeilingReason, (await units.StepAsync(region, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).Error, StringComparison.Ordinal);
        Assert.Equal(OrganizationUnitWorkflowAttribute.WithinCeilingReason, (await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).Error);
        Assert.Equal(OrganizationUnitWorkflowAttribute.WithinCeilingReason, (await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).Error);
        Assert.Equal(OrganizationUnitWorkflowAttribute.WithinCeilingReason, (await units.DeleteAsync(branch, cancellationToken)).Error);
        Assert.Equal((OrganizationUnitState.Active, OrganizationUnitState.Active), (await StateOfAsync(region, cancellationToken), await StateOfAsync(branch, cancellationToken)));
        await using (var asHolder = ServiceHost.Create(this.database, scenario.A.RuntimeOf(holder)))
        {
            Assert.True(await asHolder.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken));
        }

        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var done = await owner.Get<OrganizationService>().StepAsync(region, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken);
            Assert.True(done.IsSuccess, done.Error);
        }

        await using (var asHolder = ServiceHost.Create(this.database, scenario.A.RuntimeOf(holder)))
        {
            Assert.False(await asHolder.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_unit_out_of_use_takes_no_new_holding_and_its_holdings_grant_nothing_while_the_root_always_grants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var settings = await AddRoleAsync(scenario.A, "Settings", cancellationToken, RolePermission.SettingsView);
        var users = await AddRoleAsync(scenario.A, "Users", cancellationToken, RolePermission.UsersView);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var tenants = host.Get<RoleHoldingService>();
        var archive = await units.CreateUnitIdAsync("Old office", OrganizationUnitType.Branch, rootId, cancellationToken);
        Assert.True((await tenants.AssignUserToRoleAsync(colleague, archive, settings, scenario.A.TenantId)).IsSuccess);
        Assert.True((await tenants.AssignUserToRoleAsync(colleague, rootId, users, scenario.A.TenantId)).IsSuccess);
        Assert.True(await HoldsAsync(scenario.A, colleague, RolePermission.SettingsView, cancellationToken));

        Assert.True((await units.StepAsync(archive, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).IsSuccess);

        Assert.False(await HoldsAsync(scenario.A, colleague, RolePermission.SettingsView, cancellationToken));
        Assert.True(await HoldsAsync(scenario.A, colleague, RolePermission.UsersView, cancellationToken));
        Assert.Equal(RoleHoldingService.InactiveUnitTakesNoRoles, (await tenants.AssignUserToRoleAsync(colleague, archive, users, scenario.A.TenantId)).Error);
        Assert.DoesNotContain((await tenants.GetRoleHoldingChoicesAsync(scenario.A.TenantId, cancellationToken)).Value!.Units, unit => unit.Id == archive);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Every_unit_step_runs_under_the_tenants_structure_lock_and_the_footer_takes_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var probe = new LockProbe();
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton<IStepPreparation>(probe));
        var units = host.Get<OrganizationService>();
        var office = await units.CreateUnitIdAsync("Probed office", OrganizationUnitType.Branch, rootId, cancellationToken);

        probe.Modes.Clear();
        Assert.True((await host.Get<WorkflowService>().GetAvailableTransitionsAsync<OrganizationUnitState, OrganizationUnit>(office, OrganizationUnitWorkflowQueries.WithWorkflowGraph, cancellationToken)).IsSuccess);
        Assert.Equal(["NoTransaction"], probe.Modes);

        probe.Modes.Clear();
        Assert.True((await units.StepAsync(office, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);
        Assert.True((await units.StepAsync(office, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).IsSuccess);
        Assert.True((await units.EditAsync(office, unit => unit.Name = "Probed and renamed", cancellationToken)).IsSuccess);
        Assert.Equal(["Exclusive", "Exclusive", "Exclusive"], probe.Modes);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_data_context_refuses_a_unit_written_past_the_workflow_where_a_unit_of_its_state_may_not_sit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        Guid inactive, archived;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = owner.Get<OrganizationService>();
            inactive = await units.CreateUnitIdAsync("Paused", OrganizationUnitType.Region, rootId, cancellationToken);
            Assert.True((await units.StepAsync(inactive, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);
            archived = await units.CreateUnitIdAsync("Stored", OrganizationUnitType.Region, rootId, cancellationToken);
            Assert.True((await units.StepAsync(archived, OrganizationUnitWorkflowAttribute.ArchiveStep, cancellationToken)).IsSuccess);
        }

        Assert.Equal(OrganizationStructurePolicy.InUseBelowOutOfUseReason("Planted"), await RefusalOfInsertAsync(scenario.A, "Planted", inactive, OrganizationUnitState.Active, cancellationToken));
        Assert.Equal(OrganizationStructurePolicy.BelowArchivedReason("Planted"), await RefusalOfInsertAsync(scenario.A, "Planted", archived, OrganizationUnitState.Inactive, cancellationToken));
        Assert.Null(await RefusalOfInsertAsync(scenario.A, "Kept for the record", archived, OrganizationUnitState.Archived, cancellationToken));
        Assert.Null(await RefusalOfInsertAsync(scenario.A, "Paused team", inactive, OrganizationUnitState.Inactive, cancellationToken));
    }

    private static async Task<IReadOnlyList<string>> BlockingReasonsAsync(WorkflowService workflows, Guid unitId, string key, CancellationToken cancellationToken)
    {
        var answers = await workflows.GetAvailableTransitionsAsync<OrganizationUnitState, OrganizationUnit>(unitId, OrganizationUnitWorkflowQueries.WithWorkflowGraph, cancellationToken);
        Assert.True(answers.IsSuccess, answers.Error);
        return answers.Value!.Single(answer => answer.Transition.Key == key).BlockingReasons;
    }

    private async Task<string?> RefusalOfInsertAsync(ScenarioTenant tenant, string name, Guid parentId, OrganizationUnitState state, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        context.OrganizationUnits.Add(new OrganizationUnit { TenantId = tenant.TenantId, ParentId = parentId, Name = name, UnitType = OrganizationUnitType.Team, State = state, CreatedById = tenant.OwnerId });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (OrganizationStructureException refused)
        {
            return refused.Message;
        }
    }

    private async Task<string?> RefusalOfStateAsync(Guid unitId, OrganizationUnitState state, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var unit = await context.OrganizationUnits.SingleAsync(row => row.Id == unitId, cancellationToken);
        context.NameEditor(unit, unit.CreatedById);
        using (context.BeginTransitionSaveScope([unit], "test: a state written past the workflow"))
        {
            unit.State = state;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return null;
            }
            catch (OrganizationStructureException refused)
            {
                return refused.Message;
            }
        }
    }

    private async Task<string?> RefusalOfHoldingAsync(ScenarioTenant tenant, Guid userId, Guid roleId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = tenant.TenantId, UserId = userId, TenantRoleId = roleId, OrganizationUnitId = unitId, CreatedById = tenant.OwnerId });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (OrganizationStructureException refused)
        {
            return refused.Message;
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_platform_console_reads_a_tenants_unit_but_neither_saves_nor_deletes_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        Guid leaf;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            leaf = await owner.Get<OrganizationService>().CreateUnitIdAsync("Leaf office", OrganizationUnitType.Branch, rootId, cancellationToken);
        }

        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var units = console.Get<OrganizationService>();
        foreach (var step in new[] { OrganizationUnitWorkflowAttribute.SaveStep, OrganizationUnitWorkflowAttribute.DeleteStep })
        {
            var loaded = await units.LoadOrganizationUnitUpdaterAsync(leaf, cancellationToken);
            Assert.True(loaded.IsSuccess, loaded.Error);
            loaded.Value!.Entity.Name = "Changed on the console";
            Assert.Equal(OrganizationService.ChangeInOwnTenantOnly, (await units.UpdateOrganizationUnitAndTransitionAsync(loaded.Value, step, null, cancellationToken)).Error);
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.OrganizationUnits.AsNoTracking().SingleAsync(unit => unit.Id == leaf, cancellationToken);
        Assert.Equal(("Leaf office", OrganizationUnitState.Active), (stored.Name, stored.State));
        Assert.Equal([OrganizationUnitWorkflowAttribute.CreateStep], await check.StateHistoryEntries.Where(entry => entry.EntityId == leaf).Select(entry => entry.TransitionKey).ToListAsync(cancellationToken));
    }

    private async Task<(TwoTenantScenario Scenario, Guid RootId)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, [.. unitAdministrator, RolePermission.SettingsEdit, RolePermission.SettingsView]);
        return (scenario, await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken));
    }

    private async Task<Guid> AddRoleAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken, params RolePermission[] permissions)
    {
        await using var context = this.database.CreateSystemContext();
        var role = new TenantRole
        {
            TenantId = tenant.TenantId,
            Name = TwoTenantScenario.UniqueName(name),
            State = TenantRoleState.Active,
            CreatedById = tenant.OwnerId,
            Permissions = [.. permissions.Select(permission => new TenantRolePermission { TenantId = tenant.TenantId, Permission = permission, CreatedById = tenant.OwnerId })],
        };
        context.TenantRoles.Add(role);
        await context.SaveChangesAsync(cancellationToken);
        return role.Id;
    }

    private async Task<OrganizationUnitState> StateOfAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.OrganizationUnits.Where(unit => unit.Id == unitId).Select(unit => unit.State).SingleAsync(cancellationToken);
    }

    private async Task<bool> HoldsAsync(ScenarioTenant tenant, Guid userId, RolePermission permission, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, tenant.RuntimeOf(userId));
        return await host.Get<PermissionService>().HasPermissionAsync(permission, cancellationToken);
    }

    private static async Task<string> StructureLockModeAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken) =>
        context.Database.CurrentTransaction is null
            ? "NoTransaction"
            : await context.Database.SqlQueryRaw<string>("SELECT APPLOCK_MODE('public', {0}, 'Transaction') AS [Value]", AppLock.Structure.Resource(tenantId)).SingleAsync(cancellationToken);

    private sealed class TestUnitBlocker : IDeleteBlocker
    {
        public const string Reason = "A test record still uses this unit: release it first, or archive the unit instead.";

        private readonly Guid used;

        public TestUnitBlocker(Guid used)
        {
            this.used = used;
        }

        public List<(bool InTransaction, string LockMode)> Calls { get; } = [];

        public Type EntityType => typeof(OrganizationUnit);

        public IReadOnlyList<Type> Dependents { get; } = [];

        public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
        {
            Calls.Add((context.Database.CurrentTransaction is not null, await StructureLockModeAsync(context, ((OrganizationUnit)entity).TenantId, cancellationToken)));
            return entity.Id == this.used ? Reason : null;
        }
    }

    private sealed class LockProbe : IStepPreparation
    {
        public List<string> Modes { get; } = [];

        public Type EntityType => typeof(OrganizationUnit);

        public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
        {
            var tenantId = await context.OrganizationUnits.AsNoTracking().Where(unit => unit.Id == entityId).Select(unit => unit.TenantId).SingleAsync(cancellationToken);
            Modes.Add(await StructureLockModeAsync(context, tenantId, cancellationToken));
        }
    }
}
