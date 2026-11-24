using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data.Seed;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class DatabaseSeederTests : IClassFixture<RealDatabase>
{
    private static readonly DateTimeOffset firstStart = new(2026, 9, 1, 7, 30, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public DatabaseSeederTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_fresh_start_seeds_rows_stamped_by_their_own_tenant_and_a_second_start_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeClock(firstStart);

        var (answered, contextLog) = await SeedingHost.SeedAnsweringAsync(this.database, clock, includeDevelopmentData: true, cancellationToken);
        Assert.Contains("System scope opened for seed (depth 1).", contextLog);
        Assert.Contains("System scope closed for seed (depth 0).", contextLog);
        var afterFirstStart = await SeedingHost.SnapshotAsync(this.database, cancellationToken);

        await using (var context = this.database.CreateSystemContext())
        {
            var tenants = await context.Tenants.OrderBy(tenant => tenant.Name).ToListAsync(cancellationToken);
            var tenantOfUser = await context.Users.ToDictionaryAsync(user => user.Id, user => user.TenantId, cancellationToken);
            var platformOwner = await context.Users.SingleAsync(user => user.Email == "admin@staffora.system", cancellationToken);
            var rows = await TenantRowsAsync(context, cancellationToken);

            Assert.Equal([DatabaseSeeder.PlatformTenantName], tenants.Select(tenant => tenant.Name));
            Assert.All(tenants, tenant => Assert.Equal(TenantState.Active, tenant.State));
            Assert.Equal(DatabaseSeeder.PlatformTenantName, Assert.Single(tenants, tenant => tenant.IsRootTenant).Name);
            Assert.Equal(tenants.Single(tenant => tenant.IsRootTenant).Id, platformOwner.TenantId);
            Assert.Equal(platformOwner.Id, answered);
            Assert.Equal(1, await context.Users.ExceptSystemAccounts().CountAsync(cancellationToken));

            var systemAccounts = await context.Users.Where(user => user.IsSystemAccount).ToListAsync(cancellationToken);
            Assert.Equal(tenants.Select(tenant => (SystemAccount.IdFor(tenant.Id), tenant.Id)).Order(), systemAccounts.Select(account => (account.Id, account.TenantId)).Order());
            Assert.All(systemAccounts, account => Assert.Null(account.PasswordHash));

            var units = await context.OrganizationUnits.AsNoTracking().ToListAsync(cancellationToken);
            Assert.All(tenants, tenant =>
            {
                var root = Assert.Single(units, unit => unit.TenantId == tenant.Id && unit.UnitType == OrganizationUnitType.Company);
                Assert.Equal((tenant.Name, (Guid?)null, SystemAccount.IdFor(tenant.Id)), (root.Name, root.ParentId, root.CreatedById));
            });
            Assert.All(units.Where(unit => unit.UnitType != OrganizationUnitType.Company), unit => Assert.Equal(
                units.Single(root => root.TenantId == unit.TenantId && root.UnitType == OrganizationUnitType.Company).Id, unit.ParentId));
            Assert.All(await context.OrganizationUnitUserRoles.ToListAsync(cancellationToken), holding =>
                Assert.Equal(OrganizationUnitType.Company, units.Single(unit => unit.Id == holding.OrganizationUnitId).UnitType));

            Assert.Equal(["OrganizationUnit"], rows.Select(row => row.Type).Distinct().Order());
            Assert.All(rows, row =>
            {
                Assert.NotEqual(Guid.Empty, row.CreatedById);
                Assert.Equal(row.TenantId, tenantOfUser[row.CreatedById]);
                Assert.Equal(firstStart.UtcDateTime, row.Created);
                Assert.Null(row.ImpersonatedById);
            });

            Assert.All(tenants, tenant =>
            {
                Assert.Equal(platformOwner.Id, tenant.CreatedById);
                Assert.Equal(firstStart.UtcDateTime, tenant.Created);
            });
        }

        clock.Advance(TimeSpan.FromDays(1));
        await SeedingHost.SeedAsync(this.database, clock, includeDevelopmentData: true, cancellationToken);

        Assert.Equal(afterFirstStart, await SeedingHost.SnapshotAsync(this.database, cancellationToken));
    }

    private static async Task<List<(string Type, Guid TenantId, DateTime Created, Guid CreatedById, Guid? ImpersonatedById)>> TenantRowsAsync(TestDataContext context, CancellationToken cancellationToken)
    {
        var rows = new List<(string, Guid, DateTime, Guid, Guid?)>();
        rows.AddRange((await context.OrganizationUnits.ToListAsync(cancellationToken)).Select(Row));
        rows.AddRange((await context.TenantRoles.ToListAsync(cancellationToken)).Select(Row));
        rows.AddRange((await context.TenantRolePermissions.ToListAsync(cancellationToken)).Select(Row));
        rows.AddRange((await context.OrganizationUnitUserRoles.ToListAsync(cancellationToken)).Select(Row));
        return rows;
    }

    private static (string, Guid, DateTime, Guid, Guid?) Row(ITenantEntity row) => (row.GetType().Name, row.TenantId, row.Created, row.CreatedById, row.ImpersonatedById);
}
