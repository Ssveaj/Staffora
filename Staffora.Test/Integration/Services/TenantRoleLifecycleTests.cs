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
public sealed class TenantRoleLifecycleTests : IClassFixture<RealDatabase>
{
    private static readonly RolePermission[] roleAdministrator =
        [RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesEdit, RolePermission.RolesDelete, RolePermission.UsersView, RolePermission.SettingsView];

    private readonly RealDatabase database;

    public TenantRoleLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Deleting_an_assigned_role_is_refused_with_the_reason_and_goes_once_nobody_holds_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();
        var created = await roles.CreateAsync(RoleSteps.Draft("Coordinator", RolePermission.UsersView, RolePermission.SettingsView), cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        var role = created.Value;
        var holding = await holdings.AssignUserToRoleAsync(colleague, rootId, role, scenario.A.TenantId);
        Assert.True(holding.IsSuccess, holding.Error);

        Assert.Equal(RoleHoldersBlockRoleDelete.Reason(1), (await roles.DeleteRoleAsync(role, cancellationToken)).Error);
        await using (var check = this.database.CreateSystemContext())
        {
            var stored = await check.TenantRoles.AsNoTracking().Include(row => row.Permissions).SingleAsync(row => row.Id == role, cancellationToken);
            Assert.Equal((TenantRoleState.Active, false, 2), (stored.State, stored.IsInTransition, stored.Permissions.Count));
            Assert.True(await check.OrganizationUnitUserRoles.AnyAsync(row => row.Id == holding.Value!.Id, cancellationToken));
            Assert.False(await check.StateHistoryEntries.AnyAsync(entry => entry.EntityId == role && entry.TransitionKey == TenantRoleWorkflowAttribute.DeleteStep, cancellationToken));
        }

        await using (var asHolder = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            Assert.True(await asHolder.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsView, cancellationToken));
        }

        var answers = await host.Get<WorkflowService>().GetAvailableTransitionsAsync<TenantRoleState, TenantRole>(role, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken);
        Assert.Contains(RoleHoldersBlockRoleDelete.Reason(1), answers.Value!.Single(answer => answer.Transition.Key == TenantRoleWorkflowAttribute.DeleteStep).BlockingReasons);

        var second = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Second", cancellationToken);
        var secondHolding = await holdings.AssignUserToRoleAsync(second, rootId, role, scenario.A.TenantId);
        Assert.Equal(RoleHoldersBlockRoleDelete.Reason(2), (await roles.DeleteRoleAsync(role, cancellationToken)).Error);
        Assert.True((await holdings.RemoveUserFromRoleAsync(holding.Value!.Id)).IsSuccess);
        Assert.True((await holdings.RemoveUserFromRoleAsync(secondHolding.Value!.Id)).IsSuccess);
        var deleted = await roles.DeleteRoleAsync(role, cancellationToken);
        Assert.True(deleted.IsSuccess, deleted.Error);

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.TenantRoles.AnyAsync(row => row.Id == role, cancellationToken));
            Assert.False(await check.TenantRolePermissions.AnyAsync(row => row.TenantRoleId == role, cancellationToken));
            Assert.Equal(
                [TenantRoleWorkflowAttribute.CreateStep, TenantRoleWorkflowAttribute.DeleteStep],
                await check.StateHistoryEntries.Where(entry => entry.EntityId == role).OrderBy(entry => entry.Created).Select(entry => entry.TransitionKey).ToListAsync(cancellationToken));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_is_created_and_saved_through_its_workflow_and_its_saves_are_in_its_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();

        var created = await roles.CreateAsync(RoleSteps.Draft("  Planner  ", RolePermission.UsersView), cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        var saved = await roles.EditRoleAsync(created.Value, role => { role.Description = "Plans the week"; role.Grant(RolePermission.SettingsView); role.Revoke(RolePermission.UsersView); }, cancellationToken);
        Assert.True(saved.IsSuccess, saved.Error);

        await using var check = this.database.CreateSystemContext();
        var stored = await check.TenantRoles.AsNoTracking().Include(row => row.Permissions).SingleAsync(row => row.Id == created.Value, cancellationToken);
        Assert.Equal(("Planner", "Plans the week", TenantRoleState.Active), (stored.Name, stored.Description, stored.State));
        Assert.Equal([RolePermission.SettingsView], stored.Permissions.Select(permission => permission.Permission));
        Assert.Equal(
            [TenantRoleWorkflowAttribute.CreateStep, TenantRoleWorkflowAttribute.SaveStep],
            await check.StateHistoryEntries.Where(entry => entry.EntityId == created.Value).OrderBy(entry => entry.Created).Select(entry => entry.TransitionKey).ToListAsync(cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_delete_checks_RolesDelete_in_the_service_and_in_the_steps_gate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        Guid role;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            role = (await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Unheld", RolePermission.UsersView), cancellationToken)).Value;
        }

        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Editor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor));

        Assert.Equal("You do not have permission to delete roles.", (await host.Get<TenantRoleService>().DeleteRoleAsync(role, cancellationToken)).Error);
        var gate = await host.Get<WorkflowService>().TransitionEntityAsync<TenantRoleState, TenantRole>(role, TenantRoleWorkflowAttribute.DeleteStep, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Contains("Delete Roles", gate.Error, StringComparison.Ordinal);

        await using var check = this.database.CreateSystemContext();
        Assert.True(await check.TenantRoles.AnyAsync(row => row.Id == role, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_delete_counts_its_holders_under_the_tenants_structure_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        var probe = new LockProbe();
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton<IDeleteBlocker>(probe));
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();
        var role = (await roles.CreateAsync(RoleSteps.Draft("Probed", RolePermission.UsersView), cancellationToken)).Value;

        Assert.True((await roles.DeleteRoleAsync(role, cancellationToken)).IsSuccess);

        Assert.Equal(["Exclusive"], probe.Modes);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_that_is_not_active_grants_nothing_and_is_given_to_nobody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        Guid role;
        await using (var system = this.database.CreateSystemContext())
        {
            var row = new TenantRole { TenantId = scenario.A.TenantId, Name = "Half made", CreatedById = scenario.A.OwnerId, Permissions = [new TenantRolePermission { TenantId = scenario.A.TenantId, Permission = RolePermission.SettingsView, CreatedById = scenario.A.OwnerId }] };
            system.TenantRoles.Add(row);
            system.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = scenario.A.TenantId, UserId = colleague, OrganizationUnitId = rootId, TenantRole = row, CreatedById = scenario.A.OwnerId });
            await system.SaveChangesAsync(cancellationToken);
            role = row.Id;
        }

        await using (var asColleague = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            Assert.False(await asColleague.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsView, cancellationToken));
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();
        Assert.True(await host.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsView, cancellationToken));
        Assert.Equal("The role was not found in this tenant.", (await holdings.AssignUserToRoleAsync(scenario.A.OwnerId, rootId, role, scenario.A.TenantId)).Error);
        Assert.DoesNotContain((await holdings.GetRoleHoldingChoicesAsync(scenario.A.TenantId, cancellationToken)).Value!.Roles, option => option.Id == role);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_delete_racing_a_new_holding_of_it_lets_exactly_one_through_with_a_rule_never_a_database_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        for (var round = 0; round < 12; round++)
        {
            Guid role;
            await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
            {
                var created = await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft(TwoTenantScenario.UniqueName("Contested"), RolePermission.UsersView), cancellationToken);
                Assert.True(created.IsSuccess, created.Error);
                role = created.Value;
            }

            await using var deleting = ServiceHost.Create(this.database, scenario.A.Runtime());
            await using var giving = ServiceHost.Create(this.database, scenario.A.Runtime());
            await deleting.Get<PermissionService>().HasPermissionAsync(RolePermission.RolesDelete, cancellationToken);
            await giving.Get<PermissionService>().HasPermissionAsync(RolePermission.RolesEdit, cancellationToken);
            var delete = Task.Run(() => deleting.Get<TenantRoleService>().DeleteRoleAsync(role, cancellationToken), cancellationToken);
            var give = Task.Run(() => giving.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, rootId, role, scenario.A.TenantId), cancellationToken);
            await Task.WhenAll(delete, give);
            var (deleted, given) = (await delete, await give);

            var outcome = $"round {round}: delete {deleted.IsSuccess} '{deleted.Error}', holding {given.IsSuccess} '{given.Error}'";
            Assert.True(deleted.IsSuccess ^ given.IsSuccess, outcome);
            Assert.DoesNotContain("An error occurred", deleted.Error + given.Error, StringComparison.Ordinal);
            await using var check = this.database.CreateSystemContext();
            var roleKept = await check.TenantRoles.AnyAsync(row => row.Id == role, cancellationToken);
            var held = await check.OrganizationUnitUserRoles.AnyAsync(row => row.TenantRoleId == role, cancellationToken);
            Assert.Equal((!deleted.IsSuccess, given.IsSuccess), (roleKept, held));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_platform_console_reads_a_tenants_role_but_neither_saves_nor_deletes_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        Guid role;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var created = await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Coordinator", RolePermission.UsersView), cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            role = created.Value;
        }

        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var tenants = console.Get<TenantRoleService>();

        async Task<DetachedEntityUpdater<TenantRole>> EditedAsync()
        {
            var loaded = await tenants.LoadTenantRoleUpdaterAsync(role, cancellationToken);
            Assert.True(loaded.IsSuccess, loaded.Error);
            loaded.Value!.Entity.Name = "Changed on the console";
            loaded.Value.Entity.Grant(RolePermission.SettingsView);
            return loaded.Value;
        }

        Assert.Equal(TenantRoleService.RoleChangeInOwnTenantOnly, (await tenants.UpdateTenantRoleAndTransitionAsync(await EditedAsync(), TenantRoleWorkflowAttribute.SaveStep, null, cancellationToken)).Error);
        Assert.Equal(TenantRoleService.RoleChangeInOwnTenantOnly, (await tenants.UpdateTenantRoleAndTransitionAsync(await EditedAsync(), TenantRoleWorkflowAttribute.DeleteStep, null, cancellationToken)).Error);

        var workflows = console.Get<WorkflowService>();
        var saved = await workflows.SaveAndTransitionAsync<TenantRoleState, TenantRole>(await EditedAsync(), TenantRoleWorkflowAttribute.SaveStep, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Contains(TenantRoleWorkflowAttribute.OwnTenantReason, saved.Error, StringComparison.Ordinal);
        var deleted = await workflows.TransitionEntityAsync<TenantRoleState, TenantRole>(role, TenantRoleWorkflowAttribute.DeleteStep, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Contains(TenantRoleWorkflowAttribute.OwnTenantReason, deleted.Error, StringComparison.Ordinal);

        await using var check = this.database.CreateSystemContext();
        var stored = await check.TenantRoles.AsNoTracking().Include(row => row.Permissions).SingleAsync(row => row.Id == role, cancellationToken);
        Assert.Equal(("Coordinator", TenantRoleState.Active), (stored.Name, stored.State));
        Assert.Equal([RolePermission.UsersView], stored.Permissions.Select(permission => permission.Permission));
        Assert.Equal([TenantRoleWorkflowAttribute.CreateStep], await check.StateHistoryEntries.Where(entry => entry.EntityId == role).Select(entry => entry.TransitionKey).ToListAsync(cancellationToken));
    }

    private async Task<(TwoTenantScenario Scenario, Guid RootId)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, roleAdministrator);
        return (scenario, await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken));
    }

    private sealed class LockProbe : IDeleteBlocker
    {
        public List<string> Modes { get; } = [];

        public Type EntityType => typeof(TenantRole);

        public IReadOnlyList<Type> Dependents { get; } = [];

        public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
        {
            Modes.Add(await context.Database.SqlQueryRaw<string>("SELECT APPLOCK_MODE('public', {0}, 'Transaction') AS [Value]", AppLock.Structure.Resource(((TenantRole)entity).TenantId)).SingleAsync(cancellationToken));
            return null;
        }
    }
}
