using Microsoft.EntityFrameworkCore;

namespace Staffora.Test.Integration.Infrastructure;

[Trait("Category", "Integration")]
public class TwoTenantScenarioTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TwoTenantScenarioTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_scenario_creates_two_active_tenants_each_with_its_own_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken: cancellationToken);

        await using var context = this.database.CreateSystemContext();
        var tenants = await context.Tenants.Where(t => t.Id == scenario.A.TenantId || t.Id == scenario.B.TenantId).OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var owners = await context.Users.Where(u => u.Id == scenario.A.OwnerId || u.Id == scenario.B.OwnerId).ToDictionaryAsync(u => u.Id, u => u.TenantId, cancellationToken);

        Assert.Equal([scenario.A.Name, scenario.B.Name], tenants.Select(t => t.Name));
        Assert.All(tenants, tenant => Assert.Equal(Core.Entities.Tenancy.TenantState.Active, tenant.State));
        Assert.Equal(scenario.A.TenantId, owners[scenario.A.OwnerId]);
        Assert.Equal(scenario.B.TenantId, owners[scenario.B.OwnerId]);
    }

    [Fact]
    public async Task Each_tenant_reads_only_the_test_parents_written_in_its_own_context()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken: cancellationToken);

        foreach (var tenant in new[] { scenario.A, scenario.B })
        {
            await using var writer = this.database.CreateContext(tenant.Runtime());
            writer.TestParents.Add(new TestParent { Name = $"Parent of {tenant.Name}" });
            await writer.SaveChangesAsync(cancellationToken);
        }

        await using var readerA = this.database.CreateContext(scenario.A.Runtime());
        var seenByA = await readerA.TestParents.ToListAsync(cancellationToken);

        var parent = Assert.Single(seenByA);
        Assert.Equal($"Parent of {scenario.A.Name}", parent.Name);
        Assert.Equal(scenario.A.TenantId, parent.TenantId);
        Assert.Equal(scenario.A.OwnerId, parent.CreatedById);
    }
}
