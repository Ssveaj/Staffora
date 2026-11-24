using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data.Seed;
using Staffora.Services.Authorization;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class PlatformBootstrapTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public PlatformBootstrapTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_bootstrap_on_an_empty_non_Development_database_creates_exactly_one_owner_in_an_Active_platform_tenant_and_a_second_run_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await SeedingHost.SeedAsync(this.database, TimeProvider.System, includeDevelopmentData: false, cancellationToken);
        await using (var empty = this.database.CreateSystemContext())
        {
            Assert.False(await empty.Tenants.AnyAsync(cancellationToken));
            Assert.False(await empty.Users.AnyAsync(cancellationToken));
        }

        PlatformOwnerCredentials credentials;
        await using (var host = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var first = await host.Get<PlatformBootstrap>().BootstrapAsync("first.owner@example.com", cancellationToken);
            Assert.True(first.IsSuccess, first.Error);
            credentials = first.Value!;
            Assert.Equal("first.owner@example.com", credentials.Email);
            Assert.DoesNotContain(credentials.InitialPassword, credentials.ToString(), StringComparison.Ordinal);

            var users = host.Get<UserManager<ApplicationUser>>();
            var signedIn = await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync((await users.FindByEmailAsync(credentials.Email))!, credentials.InitialPassword, lockoutOnFailure: false);
            Assert.True(signedIn.Succeeded);
        }

        await using (var check = this.database.CreateSystemContext())
        {
            var tenant = await check.Tenants.SingleAsync(cancellationToken);
            var owner = await check.Users.ExceptSystemAccounts().SingleAsync(cancellationToken);
            var account = await check.Users.SingleAsync(user => user.IsSystemAccount, cancellationToken);
            var ownerRoles = await check.UserRoles.Where(holding => holding.UserId == owner.Id).Join(check.Roles, holding => holding.RoleId, role => role.Id, (_, role) => role.Name).ToListAsync(cancellationToken);

            Assert.Equal((DatabaseSeeder.PlatformTenantName, true, TenantState.Active), (tenant.Name, tenant.IsRootTenant, tenant.State));
            Assert.Equal((tenant.Id, credentials.Email, account.Id), (owner.TenantId, owner.Email, owner.CreatedById));
            Assert.Equal((SystemAccount.IdFor(tenant.Id), tenant.Id), (account.Id, account.TenantId));
            Assert.Equal(owner.Id, tenant.CreatedById);
            Assert.Equal([AppRoles.Admin], ownerRoles);
            Assert.True((await SessionCircuit.ResolveAsync(this.database, owner.Id)).HasPlatformReach());

            var root = await check.OrganizationUnits.SingleAsync(cancellationToken);
            Assert.Equal((tenant.Id, tenant.Name, OrganizationUnitType.Company, (Guid?)null, account.Id), (root.TenantId, root.Name, root.UnitType, root.ParentId, root.CreatedById));
        }

        var before = await SeedingHost.SnapshotAsync(this.database, cancellationToken);
        foreach (var email in new[] { "first.owner@example.com", "second.owner@example.com" })
        {
            await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
            var again = await host.Get<PlatformBootstrap>().BootstrapAsync(email, cancellationToken);
            Assert.False(again.IsSuccess);
            Assert.Contains("A platform owner exists already", again.Error, StringComparison.Ordinal);
        }

        Assert.Equal(before, await SeedingHost.SnapshotAsync(this.database, cancellationToken));

        var (answered, _) = await SeedingHost.SeedAnsweringAsync(this.database, TimeProvider.System, includeDevelopmentData: true, cancellationToken);
        await using (var check = this.database.CreateSystemContext())
        {
            var platform = await check.Tenants.SingleAsync(tenant => tenant.IsRootTenant, cancellationToken);
            var platformPeople = await check.Users.ExceptSystemAccounts().Where(user => user.TenantId == platform.Id).Select(user => user.Email).ToListAsync(cancellationToken);
            var owner = await check.Users.SingleAsync(user => user.Email == credentials.Email, cancellationToken);
            Assert.Equal([credentials.Email], platformPeople);
            Assert.Equal(1, await check.UserRoles.CountAsync(cancellationToken));
            Assert.Equal(owner.Id, answered);
            Assert.Equal(0, await check.Tenants.CountAsync(tenant => !tenant.IsRootTenant, cancellationToken));
        }
    }
}
