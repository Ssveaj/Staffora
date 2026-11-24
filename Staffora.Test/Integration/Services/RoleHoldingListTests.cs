using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class RoleHoldingListTests : IClassFixture<RealDatabase>
{
    private const string NoRolesView = "You do not have permission to view role assignments.";

    private readonly RealDatabase database;

    public RoleHoldingListTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Each_list_holds_its_role_unit_or_user_and_searches_and_sorts_by_the_other_two_columns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rows) = await ArrangeAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.RolesView);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var holdings = host.Get<RoleHoldingService>();

        var ofRole = await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery(), cancellationToken));
        Assert.Equal([rows.FirstAUnitA, rows.FirstAUnitZ, rows.LastAUnitA], ofRole.Rows.Select(row => row.Id));
        Assert.All(ofRole.Rows, row => Assert.NotNull(row.Person));
        Assert.Equal([rows.LastAUnitA, rows.FirstAUnitA, rows.FirstAUnitZ], (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery { Sort = RoleHolderList.SortPerson, Descending = true }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal([rows.FirstAUnitA, rows.LastAUnitA, rows.FirstAUnitZ], (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery { Sort = RoleHolderList.SortUnit }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal([rows.FirstAUnitZ], (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery { Search = rows.UnitZName }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal(2, (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery { Search = rows.FirstEmail }, cancellationToken))).TotalCount);
        Assert.Equal(0, (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery { Search = rows.RoleAName }, cancellationToken))).TotalCount);

        var ofUnit = await PageAsync(holdings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery(), cancellationToken));
        Assert.Equal([rows.FirstAUnitA, rows.FirstZUnitA, rows.LastAUnitA], ofUnit.Rows.Select(row => row.Id));
        Assert.All(ofUnit.Rows, row => Assert.NotNull(row.Person));
        Assert.Equal([rows.FirstZUnitA, rows.FirstAUnitA, rows.LastAUnitA], (await PageAsync(holdings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery { Sort = UnitRoleHoldingList.SortRole, Descending = true }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal([rows.FirstZUnitA], (await PageAsync(holdings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery { Search = rows.RoleZName }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal(0, (await PageAsync(holdings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery { Search = rows.UnitAName }, cancellationToken))).TotalCount);

        var ofUser = await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery(), cancellationToken));
        Assert.Equal([rows.FirstAUnitA, rows.FirstAUnitZ, rows.FirstZUnitA], ofUser.Rows.Select(row => row.Id));
        Assert.All(ofUser.Rows, row => Assert.Null(row.Person));
        Assert.Equal([rows.FirstZUnitA, rows.FirstAUnitZ, rows.FirstAUnitA], (await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery { Sort = UserRoleHoldingList.SortRole, Descending = true }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal([rows.FirstAUnitA, rows.FirstZUnitA, rows.FirstAUnitZ], (await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery { Sort = UserRoleHoldingList.SortUnit }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal([rows.FirstAUnitZ], (await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery { Search = rows.UnitZName }, cancellationToken))).Rows.Select(row => row.Id));
        Assert.Equal(0, (await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery { Search = rows.FirstEmail }, cancellationToken))).TotalCount);

        var row = ofRole.Rows.First();
        Assert.Equal((rows.RoleAName, rows.UnitAName, true, scenario.A.TenantId, rows.RoleA), (row.Role, row.Unit, row.UnitInUse, row.TenantId, row.RoleId));
    }

    [Fact]
    public async Task A_holding_list_reads_only_its_tenants_rows_the_console_reads_any_tenants_and_RolesView_is_needed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rows) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.RolesView);
        await using (var other = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            var holdings = other.Get<RoleHoldingService>();
            Assert.Equal(0, (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Equal(0, (await PageAsync(holdings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Equal(0, (await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery(), cancellationToken))).TotalCount);
        }

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var holdings = console.Get<RoleHoldingService>();
            Assert.Equal(3, (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Equal(3, (await PageAsync(holdings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Equal(3, (await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Contains(console.ContextLog, message => message.Contains("Platform console: list the holders of a role of any tenant", StringComparison.Ordinal));
        }

        await using var refused = ServiceHost.Create(this.database, scenario.A.Runtime());
        var refusedHoldings = refused.Get<RoleHoldingService>();
        Assert.Equal(NoRolesView, (await refusedHoldings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery(), cancellationToken)).Error);
        Assert.Equal(NoRolesView, (await refusedHoldings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery(), cancellationToken)).Error);
        Assert.Equal(NoRolesView, (await refusedHoldings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery(), cancellationToken)).Error);
    }

    [Fact]
    public async Task An_impersonating_platform_owner_reads_only_the_impersonated_tenants_holdings_and_a_system_account_holds_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, rows) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.RolesView);
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.B.OwnerId, scenario.B.TenantId);
        await using (var host = ServiceHost.Create(this.database, impersonating))
        {
            var holdings = host.Get<RoleHoldingService>();
            Assert.Equal(0, (await PageAsync(holdings.QueryTenantRoleHoldersAsync(rows.RoleA, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Equal(0, (await PageAsync(holdings.QueryOrganizationUnitRoleHoldingsAsync(rows.UnitA, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Equal(0, (await PageAsync(holdings.QueryApplicationUserRoleHoldingsAsync(rows.First, new ListQuery(), cancellationToken))).TotalCount);
            Assert.Empty((await holdings.GetAssignmentsByUnitAsync(rows.UnitA)).Value!);
            Assert.DoesNotContain(host.ContextLog, message => message.Contains("System scope opened", StringComparison.Ordinal));
        }

        await using (var context = this.database.CreateSystemContext())
        {
            context.OrganizationUnitUserRoles.Add(Holding(scenario.A, scenario.A.SystemAccountId, rows.RoleA, rows.UnitA));
            await context.SaveChangesAsync(cancellationToken);
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.RolesView);
        await using (var tenant = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.Equal(0, (await PageAsync(tenant.Get<RoleHoldingService>().QueryApplicationUserRoleHoldingsAsync(scenario.A.SystemAccountId, new ListQuery(), cancellationToken))).TotalCount);
        }

        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        Assert.Equal(0, (await PageAsync(console.Get<RoleHoldingService>().QueryApplicationUserRoleHoldingsAsync(scenario.A.SystemAccountId, new ListQuery(), cancellationToken))).TotalCount);
    }

    private async Task<(TwoTenantScenario Scenario, Holdings Rows)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var first = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Aa", cancellationToken);
        var last = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Zz", cancellationToken);
        await using var context = this.database.CreateSystemContext();
        var unitA = Unit(scenario.A, rootId, TwoTenantScenario.UniqueName("Aa unit"));
        var unitZ = Unit(scenario.A, rootId, TwoTenantScenario.UniqueName("Zz unit"));
        var roleA = Role(scenario.A, TwoTenantScenario.UniqueName("Aa role"));
        var roleZ = Role(scenario.A, TwoTenantScenario.UniqueName("Zz role"));
        context.OrganizationUnits.AddRange(unitA, unitZ);
        context.TenantRoles.AddRange(roleA, roleZ);
        await context.SaveChangesAsync(cancellationToken);

        var firstAUnitA = Holding(scenario.A, first, roleA.Id, unitA.Id);
        var firstAUnitZ = Holding(scenario.A, first, roleA.Id, unitZ.Id);
        var firstZUnitA = Holding(scenario.A, first, roleZ.Id, unitA.Id);
        var lastAUnitA = Holding(scenario.A, last, roleA.Id, unitA.Id);
        context.OrganizationUnitUserRoles.AddRange(firstAUnitA, firstAUnitZ, firstZUnitA, lastAUnitA);
        var roleOfB = Role(scenario.B, TwoTenantScenario.UniqueName("Aa role"));
        context.TenantRoles.Add(roleOfB);
        await context.SaveChangesAsync(cancellationToken);
        context.OrganizationUnitUserRoles.Add(Holding(scenario.B, scenario.B.OwnerId, roleOfB.Id, await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.B, cancellationToken)));
        await context.SaveChangesAsync(cancellationToken);

        var firstEmail = context.Users.Where(user => user.Id == first).Select(user => user.Email!).Single();
        return (scenario, new Holdings(first, firstEmail, roleA.Id, roleA.Name, roleZ.Name, unitA.Id, unitA.Name, unitZ.Name, firstAUnitA.Id, firstAUnitZ.Id, firstZUnitA.Id, lastAUnitA.Id));
    }

    private static OrganizationUnit Unit(ScenarioTenant tenant, Guid parentId, string name) =>
        new() { TenantId = tenant.TenantId, ParentId = parentId, Name = name, UnitType = OrganizationUnitType.Team, State = OrganizationUnitState.Active, CreatedById = tenant.OwnerId };

    private static TenantRole Role(ScenarioTenant tenant, string name) =>
        new() { TenantId = tenant.TenantId, Name = name, State = TenantRoleState.Active, CreatedById = tenant.OwnerId };

    private static OrganizationUnitUserRole Holding(ScenarioTenant tenant, Guid userId, Guid roleId, Guid unitId) =>
        new() { TenantId = tenant.TenantId, UserId = userId, TenantRoleId = roleId, OrganizationUnitId = unitId, CreatedById = tenant.OwnerId };

    private static async Task<ListPage<UserRoleHoldingRow>> PageAsync(Task<Result<ListPage<UserRoleHoldingRow>>> read)
    {
        var page = await read;
        Assert.True(page.IsSuccess, page.Error);
        return page.Value!;
    }

    private sealed record Holdings(Guid First, string FirstEmail, Guid RoleA, string RoleAName, string RoleZName, Guid UnitA, string UnitAName, string UnitZName, Guid FirstAUnitA, Guid FirstAUnitZ, Guid FirstZUnitA, Guid LastAUnitA);
}
