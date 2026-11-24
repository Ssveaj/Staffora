using Microsoft.EntityFrameworkCore;

namespace Staffora.Test.Integration.Infrastructure;

[Trait("Category", "Integration")]
public class ParallelDatabasesTests
{
    private readonly DatabasePool pool;

    public ParallelDatabasesTests(DatabasePool pool)
    {
        this.pool = pool;
    }

    [Fact]
    public async Task Two_class_databases_leased_at_once_each_create_two_tenants_while_the_other_does()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = new RealDatabase(this.pool);
        await using var second = new RealDatabase(this.pool);
        await Task.WhenAll(first.InitializeAsync().AsTask(), second.InitializeAsync().AsTask());

        Assert.NotEqual(first.Name, second.Name);
        Assert.True(DatabaseNameGuard.IsTestDatabase(first.Name));
        Assert.True(DatabaseNameGuard.IsTestDatabase(second.Name));

        var scenarios = await Task.WhenAll(TwoTenantScenario.CreateAsync(first, cancellationToken), TwoTenantScenario.CreateAsync(second, cancellationToken));
        Assert.All(scenarios, scenario => Assert.NotEqual(scenario.A.TenantId, scenario.B.TenantId));

        Assert.Equal(TenantsOf(scenarios[0]), await TenantsInAsync(first, cancellationToken));
        Assert.Equal(TenantsOf(scenarios[1]), await TenantsInAsync(second, cancellationToken));
    }

    private static Guid[] TenantsOf(TwoTenantScenario scenario) => [.. new[] { scenario.A.TenantId, scenario.B.TenantId }.Order()];

    private static async Task<Guid[]> TenantsInAsync(RealDatabase database, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return [.. (await context.Tenants.AsNoTracking().Select(tenant => tenant.Id).ToListAsync(cancellationToken)).Order()];
    }
}
