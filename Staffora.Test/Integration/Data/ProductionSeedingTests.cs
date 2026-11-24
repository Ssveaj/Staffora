using Microsoft.EntityFrameworkCore;
using Staffora.Data.Seed;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class ProductionSeedingTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ProductionSeedingTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Outside_Development_only_the_Admin_role_is_seeded_and_no_tenant_or_user_with_seeded_credentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await SeedingHost.SeedAsync(this.database, new FakeClock(new DateTimeOffset(2026, 9, 1, 7, 30, 0, TimeSpan.Zero)), includeDevelopmentData: false, cancellationToken);
        var afterFirst = await SeedingHost.SnapshotAsync(this.database, cancellationToken);
        await SeedingHost.SeedAsync(this.database, new FakeClock(new DateTimeOffset(2026, 9, 2, 7, 30, 0, TimeSpan.Zero)), includeDevelopmentData: false, cancellationToken);

        await using var context = this.database.CreateSystemContext();
        Assert.Equal([DatabaseSeeder.AdminRole], await context.Roles.Select(role => role.Name).ToListAsync(cancellationToken));
        Assert.False(await context.Tenants.AnyAsync(cancellationToken));
        Assert.False(await context.Users.AnyAsync(cancellationToken));
        Assert.Equal(afterFirst, await SeedingHost.SnapshotAsync(this.database, cancellationToken));
    }
}
