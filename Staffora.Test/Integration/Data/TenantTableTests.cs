using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class TenantTableTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantTableTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_tenant_reads_only_its_own_row_and_another_tenants_id_reads_like_a_missing_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using var readerA = this.database.CreateContext(scenario.A.Runtime());

        Assert.Equal([scenario.A.TenantId], await readerA.Tenants.Select(tenant => tenant.Id).ToListAsync(cancellationToken));
        Assert.Null(await readerA.Tenants.FirstOrDefaultAsync(tenant => tenant.Id == scenario.B.TenantId, cancellationToken));
        Assert.Null(await readerA.Tenants.FirstOrDefaultAsync(tenant => tenant.Id == Guid.NewGuid(), cancellationToken));

        Assert.Equal([scenario.A.Name], await readerA.Users.Where(user => user.Id == scenario.A.OwnerId || user.Id == scenario.B.OwnerId)
            .Where(user => user.Tenant != null).Select(user => user.Tenant!.Name).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Without_a_runtime_tenant_no_tenant_is_read_and_in_system_scope_every_one_is()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var both = new[] { scenario.A.TenantId, scenario.B.TenantId };
        var signedInWithoutTenant = new RuntimeExecutionContext();
        signedInWithoutTenant.SetUser(scenario.A.OwnerId);

        await using var withoutTenant = this.database.CreateContext(signedInWithoutTenant);
        await using var withoutRuntime = this.database.CreateContext(runtime: null);
        await using var system = this.database.CreateSystemContext();

        Assert.Empty(await withoutTenant.Tenants.ToListAsync(cancellationToken));
        Assert.Empty(await withoutRuntime.Tenants.ToListAsync(cancellationToken));
        Assert.Equal(2, await system.Tenants.CountAsync(tenant => both.Contains(tenant.Id), cancellationToken));
    }

    [Fact]
    public async Task Outside_system_scope_no_tenant_row_is_written_not_even_the_own_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var before = await LoadAsync([scenario.A.TenantId, scenario.B.TenantId], cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writerA.Tenants.SingleAsync(cancellationToken)).Description = "Renamed by its own user";

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        foreach (var write in new Action<TestDataContext, Tenant>[] { (context, tenant) => tenant.Description = "Renamed from A", (context, tenant) => context.Tenants.Remove(tenant) })
        {
            await using var writerA = this.database.CreateContext(scenario.A.Runtime());
            var foreign = before.Single(tenant => tenant.Id == scenario.B.TenantId);
            var stub = new Tenant { Id = foreign.Id, Name = foreign.Name, State = foreign.State, Created = foreign.Created };
            writerA.Tenants.Attach(stub);
            write(writerA, stub);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.Tenants.Add(new Tenant { Name = "Created by a tenant", State = TenantState.Active });

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        var after = await LoadAsync([scenario.A.TenantId, scenario.B.TenantId], cancellationToken);
        Assert.Equal(before.Select(Describe), after.Select(Describe));
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.Tenants.AnyAsync(tenant => tenant.Name == "Created by a tenant", cancellationToken));
    }

    [Fact]
    public async Task In_system_scope_a_tenant_row_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var platform = this.database.CreateContext(scenario.A.Runtime()))
        using (platform.BeginSystemScope("test: the platform owner renames a tenant"))
        {
            (await platform.Tenants.SingleAsync(tenant => tenant.Id == scenario.B.TenantId, cancellationToken)).Description = "Renamed by the platform";
            await platform.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal("Renamed by the platform", (await LoadAsync([scenario.B.TenantId], cancellationToken)).Single().Description);
    }

    private static string Describe(Tenant tenant) => $"{tenant.Id} {tenant.Name} {tenant.Description} {tenant.State} {tenant.Created:O} {tenant.CreatedById}";

    private async Task<List<Tenant>> LoadAsync(Guid[] ids, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Tenants.AsNoTracking().Where(tenant => ids.Contains(tenant.Id)).OrderBy(tenant => tenant.Name).ToListAsync(cancellationToken);
    }
}
