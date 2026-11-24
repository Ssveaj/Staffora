using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class TenantReferenceTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantReferenceTests(RealDatabase database)
    {
        this.database = database;
    }

    [Theory]
    [InlineData("permission to a role")]
    [InlineData("role holding to a role")]
    [InlineData("role holding to a unit")]
    [InlineData("role holding to a user")]
    [InlineData("unit to a parent")]
    [InlineData("test child to a parent")]
    public async Task An_insert_in_A_that_refers_to_a_row_of_B_is_refused(string relationship)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);
        var before = await SnapshotAsync(scenario, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.Add(relationship switch
            {
                "permission to a role" => new TenantRolePermission { TenantRoleId = b.Role, Permission = RolePermission.UsersView },
                "role holding to a role" => new OrganizationUnitUserRole { UserId = scenario.A.OwnerId, OrganizationUnitId = a.Unit, TenantRoleId = b.Role },
                "role holding to a unit" => new OrganizationUnitUserRole { UserId = scenario.A.OwnerId, TenantRoleId = a.Role, OrganizationUnitId = b.Unit },
                "role holding to a user" => new OrganizationUnitUserRole { UserId = scenario.B.OwnerId, OrganizationUnitId = a.Unit, TenantRoleId = a.Role },
                "unit to a parent" => new OrganizationUnit { ParentId = b.Root, Name = "Planted unit", UnitType = OrganizationUnitType.Branch },
                "test child to a parent" => (ITenantEntity)new TestChild { ParentId = b.Parent, Name = "Planted child" },
                _ => throw new ArgumentOutOfRangeException(nameof(relationship)),
            });

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(before, await SnapshotAsync(scenario, cancellationToken));
    }

    [Fact]
    public async Task A_reference_to_a_missing_row_is_refused_exactly_like_one_to_a_row_of_another_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);

        var toB = await RefusalOfPermissionToAsync(scenario, b.Role, cancellationToken);
        var toNothing = await RefusalOfPermissionToAsync(scenario, Guid.NewGuid(), cancellationToken);

        Assert.Equal(toB[toB.IndexOf(':', StringComparison.Ordinal)..], toNothing[toNothing.IndexOf(':', StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task Pointing_an_own_row_at_a_row_of_another_tenant_is_refused_tracked_and_detached()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writerA.TenantRolePermissions.SingleAsync(row => row.Id == a.Permission, cancellationToken)).TenantRoleId = b.Role;

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        TenantRolePermission detached;
        await using (var system = this.database.CreateSystemContext())
        {
            detached = await system.TenantRolePermissions.AsNoTracking().SingleAsync(row => row.Id == a.Permission, cancellationToken);
        }

        detached.TenantRoleId = b.Role;
        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.Update(detached);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(a.Role, (await check.TenantRolePermissions.SingleAsync(row => row.Id == a.Permission, cancellationToken)).TenantRoleId);
    }

    [Fact]
    public async Task Adding_a_unit_of_B_as_a_child_through_the_navigation_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var mine = await writerA.OrganizationUnits.SingleAsync(unit => unit.Id == a.Unit, cancellationToken);

            var stub = new OrganizationUnit { Id = b.Unit, TenantId = scenario.A.TenantId, ParentId = b.Root, Name = "Unit", UnitType = OrganizationUnitType.Branch };
            writerA.Attach(stub);
            mine.Children.Add(stub);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(b.Root, (await check.OrganizationUnits.SingleAsync(unit => unit.Id == b.Unit, cancellationToken)).ParentId);
    }

    [Fact]
    public async Task In_system_scope_a_row_of_B_may_not_refer_to_a_row_of_A()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var before = await SnapshotAsync(scenario, cancellationToken);

        await using (var system = this.database.CreateSystemContext())
        {
            system.TenantRolePermissions.Add(new TenantRolePermission
            {
                TenantId = scenario.B.TenantId,
                CreatedById = scenario.B.OwnerId,
                TenantRoleId = a.Role,
                Permission = RolePermission.UsersView,
            });

            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(before, await SnapshotAsync(scenario, cancellationToken));
    }

    [Fact]
    public async Task References_to_stored_rows_of_the_runtime_tenant_pass()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.TenantRolePermissions.Add(new TenantRolePermission { TenantRoleId = a.Role, Permission = RolePermission.RolesView });
            writerA.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { UserId = colleague, TenantRoleId = a.Role, OrganizationUnitId = a.Unit });
            await writerA.SaveChangesAsync(cancellationToken);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(2, await check.TenantRolePermissions.CountAsync(row => row.TenantRoleId == a.Role, cancellationToken));
        Assert.Equal(2, await check.OrganizationUnitUserRoles.CountAsync(row => row.TenantRoleId == a.Role, cancellationToken));
    }

    [Fact]
    public async Task Deleting_in_B_never_removes_a_row_of_A()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.TenantRolePermissions.Add(new TenantRolePermission { TenantRoleId = b.Role, Permission = RolePermission.UsersView });

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        var before = await SnapshotAsync(scenario, cancellationToken);
        await using (var writerB = this.database.CreateContext(scenario.B.Runtime()))
        {
            writerB.OrganizationUnitUserRoles.RemoveRange(await writerB.OrganizationUnitUserRoles.Where(row => row.TenantRoleId == b.Role).ToListAsync(cancellationToken));
            await writerB.SaveChangesAsync(cancellationToken);
        }

        await using (var hostB = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            var deleted = await hostB.Get<TenantRoleService>().DeleteRoleAsync(b.Role, cancellationToken);
            Assert.True(deleted.IsSuccess, deleted.Error);
        }

        var after = await SnapshotAsync(scenario, cancellationToken);
        Assert.Equal(before.A, after.A);
        Assert.True(after.B.Permissions < before.B.Permissions);
    }

    private sealed record Seeded(Guid Root, Guid Unit, Guid Role, Guid Permission, Guid Parent);

    private async Task<Seeded> SeedAsync(ScenarioTenant tenant, CancellationToken cancellationToken)
    {
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, tenant, cancellationToken);

        await TwoTenantScenario.GrantAsync(this.database, tenant, tenant.OwnerId, cancellationToken, RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesDelete, RolePermission.UsersView);
        Guid roleId;
        await using (var host = ServiceHost.Create(this.database, tenant.Runtime()))
        {
            var created = await host.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Viewer", RolePermission.UsersView), cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            roleId = created.Value;
        }

        var unit = new OrganizationUnit { TenantId = tenant.TenantId, ParentId = rootId, Name = "Unit", UnitType = OrganizationUnitType.Branch, State = OrganizationUnitState.Active, CreatedById = tenant.OwnerId };
        await using (var system = this.database.CreateSystemContext())
        {
            system.OrganizationUnits.Add(unit);
            await system.SaveChangesAsync(cancellationToken);
        }

        await using var context = this.database.CreateContext(tenant.Runtime());
        var parent = new TestParent { Name = "Parent" };
        context.Add(parent);
        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { UserId = tenant.OwnerId, TenantRoleId = roleId, OrganizationUnitId = unit.Id });
        await context.SaveChangesAsync(cancellationToken);
        var permission = await context.TenantRolePermissions.Where(row => row.TenantRoleId == roleId).Select(row => row.Id).SingleAsync(cancellationToken);
        return new Seeded(rootId, unit.Id, roleId, permission, parent.Id);
    }

    private async Task<string> RefusalOfPermissionToAsync(TwoTenantScenario scenario, Guid role, CancellationToken cancellationToken)
    {
        await using var writerA = this.database.CreateContext(scenario.A.Runtime());
        writerA.TenantRolePermissions.Add(new TenantRolePermission { TenantRoleId = role, Permission = RolePermission.UsersView });
        return (await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken))).Message;
    }

    private sealed record TenantRows(int Units, int Permissions, int Holdings, int Children);

    private async Task<(TenantRows A, TenantRows B)> SnapshotAsync(TwoTenantScenario scenario, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();

        async Task<TenantRows> RowsOfAsync(Guid tenant) => new(
            await system.OrganizationUnits.CountAsync(row => row.TenantId == tenant, cancellationToken),
            await system.TenantRolePermissions.CountAsync(row => row.TenantId == tenant, cancellationToken),
            await system.OrganizationUnitUserRoles.CountAsync(row => row.TenantId == tenant, cancellationToken),
            await system.TestChildren.CountAsync(row => row.TenantId == tenant, cancellationToken));

        return (await RowsOfAsync(scenario.A.TenantId), await RowsOfAsync(scenario.B.TenantId));
    }
}
