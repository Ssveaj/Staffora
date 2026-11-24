using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Data;
using Staffora.Data.Configurations.Organization;
using Staffora.Services.Authorization;
using Staffora.Services.Platform;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class OrganizationTreeTests : IClassFixture<RealDatabase>
{
    private static readonly RolePermission[] unitPermissions =
        [RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.UsersView];

    private readonly RealDatabase database;

    public OrganizationTreeTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_new_tenant_has_exactly_one_Company_root_named_after_it_and_ensuring_it_again_adds_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using var context = this.database.CreateSystemContext();
        foreach (var tenant in new[] { scenario.A, scenario.B })
        {
            var root = await context.OrganizationUnits.AsNoTracking().SingleAsync(unit => unit.TenantId == tenant.TenantId, cancellationToken);
            Assert.Equal((OrganizationUnitType.Company, (Guid?)null, tenant.Name, tenant.SystemAccountId), (root.UnitType, root.ParentId, root.Name, root.CreatedById));

            var (again, created) = await CompanyRoot.EnsureAsync(context, tenant.TenantId, cancellationToken);
            Assert.Equal((root.Id, false), (again, created));
            Assert.Equal(1, await context.OrganizationUnits.CountAsync(unit => unit.TenantId == tenant.TenantId && unit.UnitType == OrganizationUnitType.Company, cancellationToken));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_database_refuses_a_second_root_a_parentless_unit_a_rooted_Company_and_a_unit_below_itself()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);

        async Task<string> RefusalAsync(Func<Guid, OrganizationUnit> unit)
        {
            await using var context = this.database.CreateSystemContext();
            context.OrganizationUnits.Add(unit(Guid.NewGuid()));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
            return refused.InnerException!.Message;
        }

        OrganizationUnit Unit(string name, OrganizationUnitType type, Guid? parentId) =>
            new() { TenantId = scenario.A.TenantId, Name = name, UnitType = type, ParentId = parentId, CreatedById = scenario.A.OwnerId };

        Assert.Contains(OrganizationUnitConfiguration.OneCompanyRootIndex, await RefusalAsync(_ => Unit("Second root", OrganizationUnitType.Company, null)));
        Assert.Contains(OrganizationUnitConfiguration.RootHasNoParentCheck, await RefusalAsync(_ => Unit("Floating", OrganizationUnitType.Team, null)));
        await using (var context = this.database.CreateSystemContext())
        {
            var unit = Unit("Self parent", OrganizationUnitType.Team, rootId);
            context.OrganizationUnits.Add(unit);
            await context.SaveChangesAsync(cancellationToken);

            var ownParent = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"UPDATE OrganizationUnits SET ParentId = {unit.Id} WHERE Id = {unit.Id}", cancellationToken));
            Assert.Contains(OrganizationUnitConfiguration.NotOwnParentCheck, ownParent.Message);
            var rootBelow = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"UPDATE OrganizationUnits SET ParentId = {unit.Id} WHERE Id = {rootId}", cancellationToken));
            Assert.Contains(OrganizationUnitConfiguration.RootHasNoParentCheck, rootBelow.Message);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal([scenario.A.Name, "Self parent"], (await check.OrganizationUnits.Where(unit => unit.TenantId == scenario.A.TenantId).Select(unit => unit.Name).ToListAsync(cancellationToken)).Order());
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_unit_is_added_below_a_parent_and_refused_without_one_as_a_second_root_or_with_a_siblings_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();

        var north = await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId));
        Assert.True(north.IsSuccess, north.Error);
        var harbour = await units.CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, north.Value!.Id));
        Assert.True(harbour.IsSuccess, harbour.Error);

        Assert.Equal("Floating needs a parent: every unit except the Company root sits below another unit.", (await units.CreateUnitAsync(Draft("Floating", OrganizationUnitType.Team, null))).Error);
        Assert.Equal("A tenant has one Company unit, the root of its tree, which comes with the tenant.", (await units.CreateUnitAsync(Draft("Second company", OrganizationUnitType.Company, rootId))).Error);
        Assert.Equal($"{scenario.A.Name} already has a unit named North.", (await units.CreateUnitAsync(Draft("north", OrganizationUnitType.Team, rootId))).Error);

        var tree = (await units.FetchUnitTreeAsync(scenario.A.TenantId)).Value!;
        Assert.Equal([(scenario.A.Name, (Guid?)null), ("Harbour office", north.Value.Id), ("North", rootId)], tree.Select(unit => (unit.Name, unit.ParentId)));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_move_below_one_of_its_own_units_is_refused_as_a_cycle_and_the_root_keeps_its_place_and_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!;
        var south = (await units.CreateUnitAsync(Draft("South", OrganizationUnitType.Region, rootId))).Value!;
        var harbour = (await units.CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, north.Id))).Value!;

        var loop = (await units.LoadUnitAsync(north.Id)).Value!;
        loop.ParentId = harbour.Id;
        Assert.Equal("North would sit below itself. A unit cannot be moved below one of its own units.", (await units.UpdateUnitAsync(loop)).Error);

        var self = (await units.LoadUnitAsync(north.Id)).Value!;
        self.ParentId = north.Id;
        Assert.Equal("North would sit below itself. A unit cannot be moved below one of its own units.", (await units.UpdateUnitAsync(self)).Error);

        var root = (await units.LoadUnitAsync(rootId)).Value!;
        root.ParentId = south.Id;
        Assert.Equal("The Company unit is the root of the tree and has no parent.", (await units.UpdateUnitAsync(root)).Error);
        root = (await units.LoadUnitAsync(rootId)).Value!;
        root.UnitType = OrganizationUnitType.Region;
        Assert.Equal($"{scenario.A.Name} needs a parent: every unit except the Company root sits below another unit.", (await units.UpdateUnitAsync(root)).Error);

        var moved = (await units.LoadUnitAsync(harbour.Id)).Value!;
        moved.ParentId = south.Id;
        Assert.True((await units.UpdateUnitAsync(moved)).IsSuccess);

        await using var check = this.database.CreateSystemContext();
        var parents = await check.OrganizationUnits.Where(unit => unit.TenantId == scenario.A.TenantId).ToDictionaryAsync(unit => unit.Name, unit => unit.ParentId, cancellationToken);
        Assert.Equal((Guid?)null, parents[scenario.A.Name]);
        Assert.Equal(rootId, parents["North"]);
        Assert.Equal(south.Id, parents["Harbour office"]);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_parent_of_another_tenant_is_refused_by_the_service_and_by_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var rootOfB = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.B, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();

        Assert.Equal("The parent of Planted was not found in this tenant.", (await units.CreateUnitAsync(Draft("Planted", OrganizationUnitType.Branch, rootOfB))).Error);
        var north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!;
        var moved = (await units.LoadUnitAsync(north.Id)).Value!;
        moved.ParentId = rootOfB;
        Assert.Equal("The parent of North was not found in this tenant.", (await units.UpdateUnitAsync(moved)).Error);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.OrganizationUnits.Add(Draft("Planted", OrganizationUnitType.Branch, rootOfB));
            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.OrganizationUnits.AnyAsync(unit => unit.ParentId == rootOfB, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_is_held_on_a_unit_and_on_the_root_but_never_twice_on_another_tenants_unit_or_on_a_grouping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var rootOfB = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.B, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var units = host.Get<OrganizationService>();
        var tenants = host.Get<RoleHoldingService>();
        var branch = (await units.CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, rootId))).Value!;
        var grouping = (await units.CreateUnitAsync(Draft("Specialists", OrganizationUnitType.Grouping, rootId))).Value!;
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken, RolePermission.UsersView);

        var onRoot = await tenants.AssignUserToRoleAsync(colleague, rootId, role, scenario.A.TenantId);
        Assert.True(onRoot.IsSuccess, onRoot.Error);
        var onUnit = await tenants.AssignUserToRoleAsync(colleague, branch.Id, role, scenario.A.TenantId);
        Assert.True(onUnit.IsSuccess, onUnit.Error);

        Assert.Equal("The user already holds this role on this unit.", (await tenants.AssignUserToRoleAsync(colleague, branch.Id, role, scenario.A.TenantId)).Error);
        Assert.Equal(OrganizationStructurePolicy.GroupingHoldsNoRoles, (await tenants.AssignUserToRoleAsync(colleague, grouping.Id, role, scenario.A.TenantId)).Error);
        Assert.Equal("The organization unit was not found in this tenant.", (await tenants.AssignUserToRoleAsync(colleague, rootOfB, role, scenario.A.TenantId)).Error);

        var regrouped = (await units.LoadUnitAsync(branch.Id)).Value!;
        regrouped.UnitType = OrganizationUnitType.Grouping;
        Assert.Equal("Harbour office holds 1 role assignment, and a grouping holds none. Remove them before making it a grouping.", (await units.UpdateUnitAsync(regrouped)).Error);

        Assert.Equal(["Colleague"], (await tenants.GetAssignmentsByUnitAsync(rootId)).Value!.Where(holding => holding.TenantRoleId == role).Select(holding => holding.User.FirstName));
        Assert.Equal(["Colleague"], (await tenants.GetAssignmentsByUnitAsync(branch.Id)).Value!.Select(holding => holding.User.FirstName));

        await using var check = this.database.CreateSystemContext();
        var held = await check.OrganizationUnitUserRoles.Where(holding => holding.TenantRoleId == role).Select(holding => holding.OrganizationUnitId).ToListAsync(cancellationToken);
        Assert.Equal(new[] { rootId, branch.Id }.Order(), held.Order());
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_grants_its_permissions_across_the_whole_tenant_whichever_unit_holds_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var other = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Other", cancellationToken);
        Guid team;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            var north = (await units.CreateUnitAsync(Draft("North", OrganizationUnitType.Region, rootId))).Value!;
            var harbour = (await units.CreateUnitAsync(Draft("Harbour office", OrganizationUnitType.Branch, north.Id))).Value!;
            team = (await units.CreateUnitAsync(Draft("Harbour team", OrganizationUnitType.Team, harbour.Id))).Value!.Id;
            var role = await AddRoleAsync(scenario.A, "User viewer", cancellationToken, RolePermission.UsersView);
            Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, team, role, scenario.A.TenantId)).IsSuccess);
        }

        await using var colleagueHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        Assert.True(await colleagueHost.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersView, cancellationToken));
        var seen = (await colleagueHost.Get<UserService>().QueryUsersAsync(new ListQuery())).Value!.Rows.Select(user => user.Id).ToHashSet();
        Assert.Superset(new HashSet<Guid> { scenario.A.OwnerId, colleague, other }, seen);

        Assert.DoesNotContain(scenario.B.OwnerId, seen);
        Assert.Equal(PlatformConsoleService.ConsoleOnly, (await colleagueHost.Get<PlatformConsoleService>().QueryTenantUsersAsync(scenario.B.TenantId, new ListQuery())).Error);
        Assert.False(await colleagueHost.Get<PermissionService>().HasPermissionAsync(RolePermission.RolesView, cancellationToken));
    }

    private async Task<(TwoTenantScenario Scenario, Guid RootId)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, unitPermissions);
        return (scenario, await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken));
    }

    private async Task<Guid> AddRoleAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken, params RolePermission[] permissions)
    {
        await using var context = this.database.CreateSystemContext();
        var role = new TenantRole
        {
            TenantId = tenant.TenantId,
            Name = name,
            State = TenantRoleState.Active,
            CreatedById = tenant.OwnerId,
            Permissions = [.. permissions.Select(permission => new TenantRolePermission { TenantId = tenant.TenantId, Permission = permission, CreatedById = tenant.OwnerId })],
        };
        context.TenantRoles.Add(role);
        await context.SaveChangesAsync(cancellationToken);
        return role.Id;
    }

    private static OrganizationUnit Draft(string name, OrganizationUnitType type, Guid? parentId) => UnitSteps.Draft(name, type, parentId);
}
