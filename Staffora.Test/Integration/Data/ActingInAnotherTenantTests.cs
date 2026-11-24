using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public sealed class ActingInAnotherTenantTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ActingInAnotherTenantTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_into_tenant_B_from_tenant_A_is_stamped_with_Bs_system_account_and_no_impersonator(bool impersonating)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        var keptInB = await AddFolderAsync(scenario.B, "Kept in B");
        var (runtime, impersonator) = await RuntimeOfAsync(scenario.A, impersonating);

        Guid intoB, ownRow;
        await using (var context = this.database.CreateContext(runtime))
        {
            Assert.Equal(scenario.A.OwnerId, context.ActorIn(scenario.A.TenantId));
            Assert.Equal(scenario.B.SystemAccountId, context.ActorIn(scenario.B.TenantId));

            using (context.BeginSystemScope("test: write into tenant B from tenant A on B's behalf"))
            {
                var written = new TestFolder { TenantId = scenario.B.TenantId, Name = "Written from A" };
                var own = new TestFolder { Name = "Written in A" };
                context.TestFolders.AddRange(written, own);
                var stored = await context.TestFolders.AsTracking().SingleAsync(folder => folder.Id == keptInB && folder.TenantId == scenario.B.TenantId, Ct);
                stored.Name = "Renamed from A";
                context.StampOnOtherTenantsBehalf();
                await context.SaveChangesAsync(Ct);
                (intoB, ownRow) = (written.Id, own.Id);
            }
        }

        await using var check = this.database.CreateSystemContext();
        var rows = await check.TestFolders.AsNoTracking().Where(folder => folder.Id == intoB || folder.Id == ownRow || folder.Id == keptInB).ToDictionaryAsync(folder => folder.Id, Ct);

        Assert.Equal((scenario.B.TenantId, scenario.B.SystemAccountId, (Guid?)null), (rows[intoB].TenantId, rows[intoB].CreatedById, rows[intoB].ImpersonatedById));

        Assert.Equal(("Renamed from A", (Guid?)scenario.B.SystemAccountId, scenario.B.OwnerId, (Guid?)null), (rows[keptInB].Name, rows[keptInB].ModifiedById, rows[keptInB].CreatedById, rows[keptInB].ImpersonatedById));

        Assert.Equal((scenario.A.TenantId, scenario.A.OwnerId, impersonator), (rows[ownRow].TenantId, rows[ownRow].CreatedById, rows[ownRow].ImpersonatedById));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_acting_as_B_a_write_into_B_from_A_is_refused_and_stores_nothing(bool impersonating)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        var (runtime, _) = await RuntimeOfAsync(scenario.A, impersonating);
        var name = $"Unstamped {Guid.NewGuid():N}";

        await using (var context = this.database.CreateContext(runtime))
        {
            using var lease = context.BeginSystemScope("test: write into tenant B from tenant A as A's user");
            context.TestFolders.Add(new TestFolder { TenantId = scenario.B.TenantId, Name = name });
            var refused = await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(Ct));
            Assert.Contains("not a user of the row's tenant", refused.Message);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.TestFolders.AnyAsync(folder => folder.Name == name, Ct));
    }

    [Fact]
    public async Task A_managed_tenant_names_its_staffing_company_once_never_itself_and_neither_reads_the_other()
    {
        var company = await TwoTenantScenario.AddTenantAsync(this.database, "Staffing company", Ct);
        var other = await TwoTenantScenario.AddTenantAsync(this.database, "Other staffing company", Ct);
        var managed = await TwoTenantScenario.AddTenantAsync(this.database, "Brightpath Learning", Ct);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET ParentTenantId = {company.TenantId} WHERE Id = {managed.TenantId}", Ct);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: move a managed tenant to another staffing company");
            var tenant = await system.Tenants.AsTracking().SingleAsync(row => row.Id == managed.TenantId, Ct);
            Assert.Equal(company.TenantId, tenant.ParentTenantId);
            tenant.ParentTenantId = other.TenantId;
            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(Ct));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var refused = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET ParentTenantId = Id WHERE Id = {managed.TenantId}", Ct));
            Assert.Contains("CK_Tenants_ParentTenantId_NotSelf", refused.Message);
        }

        var inCompany = await AddFolderAsync(company, "Company's folder");
        var inManaged = await AddFolderAsync(managed, "Managed tenant's folder");
        await using (var companyContext = this.database.CreateContext(company.Runtime()))
        await using (var managedContext = this.database.CreateContext(managed.Runtime()))
        {
            Assert.Equal([company.TenantId], await companyContext.Tenants.Select(tenant => tenant.Id).ToListAsync(Ct));
            Assert.Equal([managed.TenantId], await managedContext.Tenants.Select(tenant => tenant.Id).ToListAsync(Ct));
            Assert.False(await companyContext.TestFolders.AnyAsync(folder => folder.Id == inManaged, Ct));
            Assert.False(await managedContext.TestFolders.AnyAsync(folder => folder.Id == inCompany, Ct));
        }

        await using var stored = this.database.CreateSystemContext();
        using var read = stored.BeginSystemScope("test: read the managed tenant's link");
        Assert.Equal(company.TenantId, await stored.Tenants.Where(tenant => tenant.Id == managed.TenantId).Select(tenant => tenant.ParentTenantId).SingleAsync(Ct));
    }

    [Fact]
    public async Task A_staffing_company_is_a_tenant_of_its_own_neither_managed_nor_the_platform_tenant_which_names_none()
    {
        var company = await TwoTenantScenario.AddTenantAsync(this.database, "Staffing company", Ct);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);

        var managed = await InsertTenantAsync("Brightpath Learning", company.TenantId);
        Assert.Equal(company.TenantId, await ParentOfAsync(managed));

        foreach (var (name, parent, root) in new[] { ("Managed by a managed tenant", managed, false), ("Managed by the platform", platform.TenantId, false), ("A second platform tenant", company.TenantId, true) })
        {
            var refused = await Assert.ThrowsAsync<TenantIsolationException>(() => InsertTenantAsync(name, parent, root));
            Assert.Contains("staffing company", refused.Message);
            await using var check = this.database.CreateSystemContext();
            using var lease = check.BeginSystemScope("test: look for a refused tenant");
            Assert.False(await check.Tenants.AnyAsync(tenant => tenant.Name.StartsWith(name), Ct));
        }
    }

    [Fact]
    public async Task A_managed_tenants_staffing_company_never_changes_in_a_transition_save_scope_or_a_bulk_update_either()
    {
        var company = await TwoTenantScenario.AddTenantAsync(this.database, "Staffing company", Ct);
        var other = await TwoTenantScenario.AddTenantAsync(this.database, "Other staffing company", Ct);
        var managed = await InsertTenantAsync("Brightpath Learning", company.TenantId);

        foreach (var (tenantId, parent) in new[] { (managed, (Guid?)other.TenantId), (managed, null), (other.TenantId, company.TenantId) })
        {
            await using var system = this.database.CreateSystemContext();
            using var lease = system.BeginSystemScope("test: a step's save on a tenant");
            var tenant = await system.Tenants.AsTracking().SingleAsync(row => row.Id == tenantId, Ct);
            tenant.ParentTenantId = parent;
            using (system.BeginTransitionSaveScope([tenant], "test: a tenant's step"))
            {
                Assert.Contains("staffing company", (await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(Ct))).Message);
            }
        }

        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: a bulk update of tenants");
            Assert.Contains("staffing company", (await Assert.ThrowsAsync<TenantIsolationException>(() => system.Tenants.Where(row => row.Id == managed).ExecuteUpdateAsync(set => set.SetProperty(row => row.ParentTenantId, other.TenantId), Ct))).Message);
        }

        Assert.Equal((company.TenantId, (Guid?)null), (await ParentOfAsync(managed), await ParentOfAsync(other.TenantId)));
    }

    private async Task<Guid> InsertTenantAsync(string name, Guid parentTenantId, bool isRootTenant = false)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: create a managed tenant");
        var tenant = new Tenant { Name = TwoTenantScenario.UniqueName(name), State = TenantState.Active, ParentTenantId = parentTenantId, IsRootTenant = isRootTenant };
        system.Tenants.Add(tenant);
        await system.SaveChangesAsync(Ct);
        return tenant.Id;
    }

    private async Task<Guid?> ParentOfAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a tenant's staffing company");
        return await system.Tenants.Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.ParentTenantId).SingleAsync(Ct);
    }

    private async Task<(IRuntimeExecutionContext Runtime, Guid? Impersonator)> RuntimeOfAsync(ScenarioTenant tenant, bool impersonating)
    {
        if (!impersonating)
        {
            return (tenant.Runtime(), null);
        }

        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var runtime = platform.PlatformOwnerRuntime();
        runtime.StartImpersonation(tenant.OwnerId, tenant.TenantId);
        return (runtime, platform.OwnerId);
    }

    private async Task<Guid> AddFolderAsync(ScenarioTenant tenant, string name)
    {
        await using var context = this.database.CreateContext(tenant.Runtime());
        var folder = new TestFolder { Name = name };
        context.TestFolders.Add(folder);
        await context.SaveChangesAsync(Ct);
        return folder.Id;
    }
}
