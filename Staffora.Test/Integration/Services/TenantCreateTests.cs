using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Settings;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class TenantCreateTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantCreateTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_created_tenant_holds_its_trimmed_name_and_description_and_its_pinned_time_zone_stamped_with_its_system_account()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var name = TwoTenantScenario.UniqueName("Brightpath Learning");

        var created = await host.Get<TenantService>().CreateTenantAsync(new Tenant { Name = $"  {name}  ", Description = "  Places staff members in the north  " }, "tenant-save-setup", Ct);

        Assert.True(created.IsSuccess, created.Error);
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read the created tenant");
        var tenant = await system.Tenants.AsNoTracking().SingleAsync(row => row.Id == created.Value, Ct);
        Assert.Equal((name, "Places staff members in the north", TenantState.Setup), (tenant.Name, tenant.Description, tenant.State));

        var zone = await system.TenantSettings.AsNoTracking().SingleAsync(row => row.TenantId == created.Value, Ct);
        Assert.Equal((TenantSettingDefinitions.TimeZone.Key, ServiceHost.PlatformTimeZone, SystemAccount.IdFor(created.Value)), (zone.Key, zone.Value, zone.CreatedById));

        Assert.True(await system.Users.AnyAsync(user => user.Id == SystemAccount.IdFor(created.Value) && user.IsSystemAccount, Ct));
        Assert.Equal(SystemAccount.IdFor(created.Value), (await system.OrganizationUnits.AsNoTracking().SingleAsync(unit => unit.TenantId == created.Value, Ct)).CreatedById);
    }

    [Fact]
    public async Task A_create_that_fails_after_its_ensures_leaves_no_tenant_account_root_or_time_zone()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var ensure = host.Get<TenantSettingsEnsureService>();
        var before = await CountsAsync();
        var inserted = Guid.Empty;

        var result = await host.Get<WorkflowService>().CreateAndTransitionAsync<TenantState, Tenant>(new Tenant { Name = TwoTenantScenario.UniqueName("Orphan") }, "tenant-save-setup", afterInsert: async (context, row, ct) =>
        {
            inserted = row.Id;
            await SystemAccountService.EnsureAsync(context, row.Id, ct);
            await CompanyRoot.EnsureAsync(context, row.Id, ct);
            Assert.True(await ensure.EnsureTimeZoneAsync(context, row.Id, ct));
            throw new InvalidOperationException("after the ensures");
        }, queryConfigurator: TenantWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);

        Assert.False(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, inserted);
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task A_failing_ensure_rolls_the_whole_create_back()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), services => services.Configure<PlatformOptions>(options => options.TimeZone = "Nowhere/Never"));
        var before = await CountsAsync();

        var created = await host.Get<TenantService>().CreateTenantAsync(new Tenant { Name = TwoTenantScenario.UniqueName("Unpinned") }, "tenant-save-setup", Ct);

        Assert.False(created.IsSuccess);
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task Inside_the_create_a_refused_insert_of_the_system_account_is_never_caught_to_carry_on()
    {
        var race = new InsertsTheRowFirst(context => context.ChangeTracker.Entries<ApplicationUser>().FirstOrDefault(entry => entry.State == EntityState.Added && entry.Entity.IsSystemAccount)?.Entity is { } account
            ? ("INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, FirstName, LastName, State, IsInTransition, IsSystemAccount, TenantId, Created, CreatedById, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, SecurityStamp) VALUES ({0}, {1}, {2}, {3}, {4}, 0, 0, 1, {5}, SYSUTCDATETIME(), {0}, 0, 0, 0, 0, 0, {6})",
                [account.Id, account.UserName!, account.NormalizedUserName!, account.FirstName, account.LastName, account.TenantId, account.SecurityStamp!])
            : null);
        await AssertTheRaceEndsTheCreateAsync(race);
    }

    [Fact]
    public async Task Inside_the_create_a_refused_insert_of_the_Company_root_is_never_caught_to_carry_on()
    {
        var race = new InsertsTheRowFirst(context => context.ChangeTracker.Entries<OrganizationUnit>().FirstOrDefault(entry => entry.State == EntityState.Added && entry.Entity.UnitType == OrganizationUnitType.Company)?.Entity is { } root
            ? ("INSERT INTO OrganizationUnits (Id, Name, UnitType, State, IsInTransition, TenantId, Created, CreatedById) VALUES ({0}, {1}, 0, 1, 0, {2}, SYSUTCDATETIME(), {3})",
                [Guid.NewGuid(), root.Name, root.TenantId, root.CreatedById])
            : null);
        await AssertTheRaceEndsTheCreateAsync(race);
    }

    private async Task AssertTheRaceEndsTheCreateAsync(InsertsTheRowFirst race)
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), interceptors: [race]);
        var before = await CountsAsync();

        var created = await host.Get<TenantService>().CreateTenantAsync(new Tenant { Name = TwoTenantScenario.UniqueName("Raced") }, "tenant-save-setup", Ct);

        Assert.True(race.Raced, "the race ran inside the create");
        Assert.True(race.Inserted, $"the race's own insert went through: {race.Failure}");
        Assert.False(created.IsSuccess);
        Assert.Equal(before, await CountsAsync());
    }

    private sealed class InsertsTheRowFirst : SaveChangesInterceptor
    {
        private readonly Func<DbContext, (string Sql, object[] Parameters)?> race;

        public InsertsTheRowFirst(Func<DbContext, (string Sql, object[] Parameters)?> race)
        {
            this.race = race;
        }

        public bool Raced { get; private set; }

        public bool Inserted { get; private set; }

        public string? Failure { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Raced && eventData.Context is { } context && this.race(context) is { } insert)
            {
                Raced = true;
                try
                {
                    Inserted = await context.Database.ExecuteSqlRawAsync(insert.Sql, insert.Parameters, cancellationToken) == 1;
                }
                catch (Exception exception)
                {
                    Failure = exception.Message;
                    throw;
                }
            }

            return result;
        }
    }

    [Fact]
    public async Task A_name_another_tenant_holds_is_refused_whatever_its_case_and_spaces_and_so_is_a_rename_to_it()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var tenants = host.Get<TenantService>();
        var name = TwoTenantScenario.UniqueName("Northfield Staff");
        Assert.True((await tenants.CreateTenantAsync(new Tenant { Name = name }, "tenant-save-setup", Ct)).IsSuccess);
        var before = await CountsAsync();

        var again = await tenants.CreateTenantAsync(new Tenant { Name = $"  {name.ToUpperInvariant()} " }, "tenant-save-setup", Ct);
        Assert.Equal(TenantService.NameTaken, again.Error);
        Assert.Equal(before, await CountsAsync());

        var other = await tenants.CreateTenantAsync(new Tenant { Name = TwoTenantScenario.UniqueName("Southfield Staff") }, "tenant-save-setup", Ct);
        var updater = (await tenants.LoadTenantUpdaterAsync(other.Value, Ct)).Value!;
        var ownName = updater.Entity.Name;
        updater.Entity.Name = name.ToLowerInvariant();
        Assert.Equal(TenantService.NameTaken, (await tenants.UpdateTenantAndTransitionAsync(updater, "tenant-save-setup", null, Ct)).Error);
        Assert.Equal(ownName, (await TenantAsync(other.Value)).Name);

        updater = (await tenants.LoadTenantUpdaterAsync(other.Value, Ct)).Value!;
        updater.Entity.Name = ownName.ToUpperInvariant() + "  ";
        Assert.True((await tenants.UpdateTenantAndTransitionAsync(updater, "tenant-save-setup", null, Ct)).IsSuccess);
        Assert.Equal(ownName.ToUpperInvariant(), (await TenantAsync(other.Value)).Name);
    }

    [Fact]
    public async Task Two_creates_of_one_name_at_once_store_one_tenant_and_refuse_the_other_in_the_same_words()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        for (var round = 0; round < 3; round++)
        {
            await using var first = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
            await using var second = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
            var name = TwoTenantScenario.UniqueName("Twin");
            var results = await Task.WhenAll(
                Task.Run(() => first.Get<TenantService>().CreateTenantAsync(new Tenant { Name = name }, "tenant-save-setup", Ct), Ct),
                Task.Run(() => second.Get<TenantService>().CreateTenantAsync(new Tenant { Name = name }, "tenant-save-setup", Ct), Ct));

            Assert.Single(results, result => result.IsSuccess);
            Assert.Equal(TenantService.NameTaken, Assert.Single(results, result => !result.IsSuccess).Error);
            await using var system = this.database.CreateSystemContext();
            using var lease = system.BeginSystemScope("test: count the twins");
            var stored = await system.Tenants.Where(tenant => tenant.Name == name).Select(tenant => tenant.Id).ToListAsync(Ct);
            Assert.Equal([results.Single(result => result.IsSuccess).Value], stored);
        }
    }

    [Fact]
    public async Task The_platform_tenants_name_and_description_are_fixed_and_it_is_never_suspended_or_scheduled_for_deletion()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var tenants = host.Get<TenantService>();
        var stored = await TenantAsync(platform.TenantId);

        foreach (var step in new[] { "tenant-save", "tenant-suspend", "tenant-schedule-deletion" })
        {
            var updater = (await tenants.LoadTenantUpdaterAsync(platform.TenantId, Ct)).Value!;
            updater.Entity.Name = stored.Name + " renamed";
            updater.Entity.Description = "Changed";
            Assert.Equal(TenantService.PlatformTenantFixed, (await tenants.UpdateTenantAndTransitionAsync(updater, step, null, Ct)).Error);
        }

        foreach (var step in new[] { "tenant-save", "tenant-suspend", "tenant-schedule-deletion" })
        {
            var updater = (await tenants.LoadTenantUpdaterAsync(platform.TenantId, Ct)).Value!;
            Assert.False((await tenants.UpdateTenantAndTransitionAsync(updater, step, null, Ct)).IsSuccess, step);
        }

        var after = await TenantAsync(platform.TenantId);
        Assert.Equal((stored.Name, stored.Description, TenantState.Active), (after.Name, after.Description, after.State));
    }

    private async Task<(int Tenants, int Users, int Units, int Settings)> CountsAsync()
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: count what a create writes");
        return (await system.Tenants.CountAsync(Ct), await system.Users.CountAsync(Ct), await system.OrganizationUnits.CountAsync(Ct), await system.TenantSettings.CountAsync(Ct));
    }

    private async Task<Tenant> TenantAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a tenant");
        return await system.Tenants.AsNoTracking().SingleAsync(tenant => tenant.Id == id, Ct);
    }
}
