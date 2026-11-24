using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class RoleCeilingTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public RoleCeilingTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_role_editor_gives_and_takes_away_no_role_beyond_their_own_permissions_themselves_included()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "RoleEditor", cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.RolesView, RolePermission.RolesEdit);
        var strong = await AddRoleAsync(scenario.A, "Strong", cancellationToken, RolePermission.UsersEdit, RolePermission.SettingsEdit, RolePermission.RolesEdit);
        var weak = await AddRoleAsync(scenario.A, "Weak", cancellationToken, RolePermission.RolesView);
        var ownersHolding = await HoldAsync(scenario.A, scenario.A.OwnerId, strong, rootId, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor));
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();

        var choices = (await holdings.GetRoleHoldingChoicesAsync(scenario.A.TenantId, cancellationToken)).Value!;
        Assert.Contains(choices.Roles, role => role.Id == weak);
        Assert.DoesNotContain(choices.Roles, role => role.Id == strong);
        Assert.False(choices.WithinCeiling(strong));
        Assert.True(choices.WithinCeiling(weak));

        Assert.Equal(RoleHoldingService.RoleBeyondCeilingNotGiven, (await holdings.AssignUserToRoleAsync(editor, rootId, strong, scenario.A.TenantId)).Error);
        Assert.Equal(RoleHoldingService.RoleBeyondCeilingNotGiven, (await holdings.AssignUserToRoleAsync(colleague, rootId, strong, scenario.A.TenantId)).Error);
        await using (var asEditor = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor)))
        {
            Assert.False(await asEditor.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersEdit, cancellationToken));
        }

        Assert.Equal(RoleHoldingService.RoleBeyondCeilingNotTaken, (await holdings.RemoveUserFromRoleAsync(ownersHolding)).Error);
        Assert.Equal(1, await HoldingsOfAsync(strong, cancellationToken));

        var given = await holdings.AssignUserToRoleAsync(colleague, rootId, weak, scenario.A.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        Assert.True((await holdings.RemoveUserFromRoleAsync(given.Value!.Id)).IsSuccess);
    }

    [Fact]
    public async Task A_role_editor_puts_on_or_takes_off_a_role_only_permissions_they_hold_and_deletes_no_role_beyond_them()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "RoleEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesEdit, RolePermission.RolesDelete);
        var weak = await AddRoleAsync(scenario.A, "Weak", cancellationToken, RolePermission.RolesView);
        var strong = await AddRoleAsync(scenario.A, "Strong", cancellationToken, RolePermission.RolesView, RolePermission.SettingsEdit);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor));
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();

        Assert.Equal(TenantRoleWorkflowAttribute.ChangesWithinCeilingReason, (await roles.EditRoleAsync(weak, raised => raised.Grant(RolePermission.UsersEdit), cancellationToken)).Error);
        var created = await roles.CreateAsync(RoleSteps.Draft(TwoTenantScenario.UniqueName("Above"), RolePermission.SettingsEdit), cancellationToken);
        Assert.Equal(TenantRoleService.RolePermissionsBeyondCeiling, created.Error);

        var stripped = await roles.EditRoleAsync(strong, role => role.Revoke(RolePermission.SettingsEdit), cancellationToken);
        Assert.Contains(TenantRoleWorkflowAttribute.StoredWithinCeilingReason, stripped.Error, StringComparison.Ordinal);
        Assert.Contains(TenantRoleWorkflowAttribute.ChangesWithinCeilingReason, stripped.Error, StringComparison.Ordinal);
        Assert.Equal(TenantRoleWorkflowAttribute.StoredWithinCeilingReason, (await roles.DeleteRoleAsync(strong, cancellationToken)).Error);
        Assert.Equal([RolePermission.RolesView], await PermissionsOfAsync(weak, cancellationToken));
        Assert.Equal(new[] { RolePermission.RolesView, RolePermission.SettingsEdit }.Order(), await PermissionsOfAsync(strong, cancellationToken));

        var within = await roles.EditRoleAsync(weak, role => role.Grant(RolePermission.RolesEdit), cancellationToken);
        Assert.True(within.IsSuccess, within.Error);
        Assert.Equal(new[] { RolePermission.RolesView, RolePermission.RolesEdit }.Order(), await PermissionsOfAsync(weak, cancellationToken));
        Assert.Equal(new[] { RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesEdit, RolePermission.RolesDelete }.Concat(RolePermissions.Personal).Order(), (await roles.GetGrantablePermissionsAsync(cancellationToken)).Order());
    }

    [Fact]
    public async Task A_role_editor_changes_nothing_of_a_role_that_grants_more_than_they_hold_and_its_dialog_is_read_only()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "RoleEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView, RolePermission.UsersEdit);
        var strong = await AddRoleAsync(scenario.A, "Strong", cancellationToken, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView, RolePermission.UsersEdit, RolePermission.SettingsEdit);
        await HoldAsync(scenario.A, scenario.A.OwnerId, strong, rootId, cancellationToken);
        string name;
        await using (var context = this.database.CreateSystemContext())
        {
            name = await context.TenantRoles.Where(role => role.Id == strong).Select(role => role.Name).SingleAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor));
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();

        Assert.Equal(TenantRoleWorkflowAttribute.StoredWithinCeilingReason, (await roles.EditRoleAsync(strong, role => { role.Revoke(RolePermission.RolesEdit); role.Revoke(RolePermission.UsersEdit); }, cancellationToken)).Error);
        Assert.Equal(TenantRoleWorkflowAttribute.StoredWithinCeilingReason, (await roles.EditRoleAsync(strong, role => role.Name = TwoTenantScenario.UniqueName("Renamed"), cancellationToken)).Error);
        Assert.Equal(TenantRoleWorkflowAttribute.StoredWithinCeilingReason, (await roles.EditRoleAsync(strong, role => role.Description = "Described by a weaker editor", cancellationToken)).Error);
        Assert.Equal(new[] { RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView, RolePermission.UsersEdit, RolePermission.SettingsEdit }.Order(), await PermissionsOfAsync(strong, cancellationToken));
        await using (var context = this.database.CreateSystemContext())
        {
            var stored = await context.TenantRoles.AsNoTracking().SingleAsync(role => role.Id == strong, cancellationToken);
            Assert.Equal((name, (string?)null), (stored.Name, stored.Description));
        }

        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.True(await owner.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersEdit, cancellationToken));
        }

        var options = await roles.GetTenantRoleEditorOptionsAsync(strong, cancellationToken);
        Assert.True(options.Value!.FieldsLocked);
        Assert.Equal(TenantRoleService.RoleBeyondCeilingReadOnly, options.Value.LockedReason);
        Assert.False(options.Value.CanManageHolders);
        var steps = (await host.Get<Staffora.Services.Workflow.WorkflowService>().GetAvailableTransitionsAsync<TenantRoleState, TenantRole>(strong, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken)).Value!;
        Assert.All(steps, step => Assert.False(step.CanRun, step.Transition.Key));
    }

    [Fact]
    public async Task A_unit_editor_neither_deactivates_nor_reactivates_a_unit_whose_holdings_grant_more_than_they_hold()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "UnitEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsEdit, RolePermission.RolesView);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        var strong = await AddRoleAsync(scenario.A, "Strong", cancellationToken, RolePermission.UsersEdit, RolePermission.SettingsEdit);
        var weak = await AddRoleAsync(scenario.A, "Weak", cancellationToken, RolePermission.RolesView);
        Guid strongUnit;
        Guid weakUnit;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.UsersEdit, RolePermission.SettingsEdit, RolePermission.RolesView);
            var ownersUnits = owner.Get<OrganizationService>();
            strongUnit = await ownersUnits.CreateUnitIdAsync(TwoTenantScenario.UniqueName("Branch"), OrganizationUnitType.Branch, rootId, cancellationToken);
            weakUnit = await ownersUnits.CreateUnitIdAsync(TwoTenantScenario.UniqueName("Office"), OrganizationUnitType.Branch, rootId, cancellationToken);
        }

        await HoldAsync(scenario.A, holder, strong, strongUnit, cancellationToken);
        await HoldAsync(scenario.A, holder, weak, weakUnit, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(editor));
        var units = host.Get<OrganizationService>();

        Assert.Equal(OrganizationUnitWorkflowAttribute.WithinCeilingReason, (await units.StepAsync(strongUnit, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).Error);
        Assert.Equal(OrganizationUnitState.Active, await StateOfAsync(strongUnit, cancellationToken));
        await using (var asHolder = ServiceHost.Create(this.database, scenario.A.RuntimeOf(holder)))
        {
            Assert.True(await asHolder.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken));
        }

        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var closed = await owner.Get<OrganizationService>().StepAsync(strongUnit, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken);
            Assert.True(closed.IsSuccess, closed.Error);
        }

        Assert.Equal(OrganizationUnitWorkflowAttribute.WithinCeilingReason, (await units.StepAsync(strongUnit, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).Error);
        Assert.Equal(OrganizationUnitState.Inactive, await StateOfAsync(strongUnit, cancellationToken));

        Assert.True((await units.StepAsync(weakUnit, OrganizationUnitWorkflowAttribute.DeactivateStep, cancellationToken)).IsSuccess);
        Assert.True((await units.StepAsync(weakUnit, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).IsSuccess);
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.True((await owner.Get<OrganizationService>().StepAsync(strongUnit, OrganizationUnitWorkflowAttribute.ReactivateStep, cancellationToken)).IsSuccess);
        }

        var renamed = await units.EditAsync(strongUnit, unit => unit.Name = TwoTenantScenario.UniqueName("Renamed branch"), cancellationToken);
        Assert.True(renamed.IsSuccess, renamed.Error);
    }

    [Fact]
    public async Task The_platform_tenants_tree_takes_no_role_holding_from_the_console()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var tenants = console.Get<RoleHoldingService>();

        Assert.False(await tenants.CanManageRoleHoldingsAsync(platform.TenantId));
        Assert.Equal(ApplicationDbContext.PlatformTreeIsRootOnlyReason, (await tenants.GetRoleHoldingChoicesAsync(platform.TenantId, cancellationToken)).Error);
        Assert.Equal(ApplicationDbContext.PlatformTreeIsRootOnlyReason, (await tenants.RemoveUserFromRoleAsync(Guid.NewGuid())).Error);
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

    private async Task<Guid> HoldAsync(ScenarioTenant tenant, Guid userId, Guid roleId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var holding = new OrganizationUnitUserRole { TenantId = tenant.TenantId, UserId = userId, TenantRoleId = roleId, OrganizationUnitId = unitId, CreatedById = tenant.OwnerId };
        context.OrganizationUnitUserRoles.Add(holding);
        await context.SaveChangesAsync(cancellationToken);
        return holding.Id;
    }

    private async Task<int> HoldingsOfAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.OrganizationUnitUserRoles.CountAsync(holding => holding.TenantRoleId == roleId, cancellationToken);
    }

    private async Task<OrganizationUnitState> StateOfAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.OrganizationUnits.Where(unit => unit.Id == unitId).Select(unit => unit.State).SingleAsync(cancellationToken);
    }

    private async Task<List<RolePermission>> PermissionsOfAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return [.. (await context.TenantRolePermissions.Where(permission => permission.TenantRoleId == roleId).Select(permission => permission.Permission).ToListAsync(cancellationToken)).Order()];
    }
}
