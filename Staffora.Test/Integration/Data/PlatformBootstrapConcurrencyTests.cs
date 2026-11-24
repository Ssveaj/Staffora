using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data.Seed;
using Staffora.Services.Authorization;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class PlatformBootstrapConcurrencyTests : IClassFixture<RealDatabase>
{
    private const int Runs = 4;

    private readonly RealDatabase database;

    public PlatformBootstrapConcurrencyTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Bootstraps_run_at_once_create_one_platform_tenant_and_one_owner_and_the_others_refuse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedingHost.SeedAsync(this.database, TimeProvider.System, includeDevelopmentData: false, cancellationToken);

        using var start = new SemaphoreSlim(0, Runs);
        var runs = Enumerable.Range(1, Runs).Select(async run =>
        {
            await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
            var bootstrap = host.Get<PlatformBootstrap>();
            await start.WaitAsync(cancellationToken);
            return await bootstrap.BootstrapAsync($"owner{run}@example.com", cancellationToken);
        }).ToList();
        start.Release(Runs);
        var results = await Task.WhenAll(runs);

        var created = Assert.Single(results, result => result.IsSuccess);
        Assert.All(results.Where(result => !result.IsSuccess), refused => Assert.Contains("A platform owner exists already", refused.Error, StringComparison.Ordinal));

        await using (var check = this.database.CreateSystemContext())
        {
            var platform = await check.Tenants.SingleAsync(cancellationToken);
            var owner = await check.Users.ExceptSystemAccounts().SingleAsync(cancellationToken);
            Assert.Equal((true, TenantState.Active, platform.Id, created.Value!.Email), (platform.IsRootTenant, platform.State, owner.TenantId, owner.Email));
            Assert.Equal(1, await check.Users.CountAsync(user => user.IsSystemAccount, cancellationToken));
            Assert.Equal(1, await check.UserRoles.CountAsync(cancellationToken));
            Assert.True((await SessionCircuit.ResolveAsync(this.database, owner.Id)).HasPlatformReach());
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.Tenants.Add(new Tenant { Name = "Second platform", State = TenantState.Active, IsRootTenant = true });
            await Assert.ThrowsAsync<DbUpdateException>(() => system.SaveChangesAsync(cancellationToken));
        }
    }
}
