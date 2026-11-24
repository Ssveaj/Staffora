using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class SystemAccountTests : IClassFixture<RealDatabase>
{
    private static readonly DateTimeOffset noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public SystemAccountTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_account_is_ensured_once_per_tenant_and_a_second_run_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var before = await AccountsOfAsync([scenario.A.TenantId, scenario.B.TenantId], cancellationToken);
        var snapshot = Snapshot(before);

        await using (var context = this.database.CreateSystemContext())
        {
            Assert.False(await SystemAccountService.EnsureAsync(context, scenario.A.TenantId, cancellationToken));
        }

        var again = await Service().EnsureAsync(scenario.B.TenantId, cancellationToken);
        Assert.True(again.IsSuccess);
        Assert.Equal(scenario.B.SystemAccountId, again.Value);

        Assert.Equal(snapshot, Snapshot(await AccountsOfAsync([scenario.A.TenantId, scenario.B.TenantId], cancellationToken)));
        Assert.Equal(
            new[] { (scenario.A.SystemAccountId, scenario.A.TenantId), (scenario.B.SystemAccountId, scenario.B.TenantId) }.Order(),
            before.Select(account => (account.Id, account.TenantId)).Order());
        Assert.All(before, account =>
        {
            Assert.True(account.IsSystemAccount);
            Assert.Null(account.PasswordHash);
            Assert.Null(account.Email);
            Assert.Equal(SystemAccount.UserNameFor(account.TenantId), account.UserName);
        });
    }

    [Fact]
    public async Task Startup_ensures_the_account_of_every_existing_tenant_that_lacks_one_and_a_second_run_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        var bare = new Tenant { Name = "Company C", State = TenantState.Active };
        await using (var context = this.database.CreateSystemContext())
        {
            context.Tenants.Add(bare);
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.Empty(await AccountsOfAsync([bare.Id], cancellationToken));

        var first = await Service().EnsureForEveryTenantAsync(cancellationToken);
        Assert.True(first.IsSuccess);
        Assert.True(first.Value!.Created >= 1);
        Assert.Empty(first.Value.FailedTenantIds);
        var created = Assert.Single(await AccountsOfAsync([bare.Id], cancellationToken));
        Assert.Equal(SystemAccount.IdFor(bare.Id), created.Id);

        var tenants = new[] { scenario.A.TenantId, scenario.B.TenantId, bare.Id };
        var afterFirst = Snapshot(await AccountsOfAsync(tenants, cancellationToken));
        var second = await Service().EnsureForEveryTenantAsync(cancellationToken);
        Assert.True(second.IsSuccess);
        Assert.Equal(0, second.Value!.Created);
        Assert.Empty(second.Value.FailedTenantIds);
        Assert.Equal(afterFirst, Snapshot(await AccountsOfAsync(tenants, cancellationToken)));
    }

    [Fact]
    public async Task An_ordinary_user_holding_the_derived_id_or_a_missing_tenant_is_refused_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var squatted = new Tenant { Name = "Company D", State = TenantState.Active };
        await using (var context = this.database.CreateSystemContext())
        {
            context.Tenants.Add(squatted);
            await context.SaveChangesAsync(cancellationToken);
            context.Users.Add(new ApplicationUser { Id = SystemAccount.IdFor(squatted.Id), UserName = "squatter", NormalizedUserName = "SQUATTER", TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId });
            await context.SaveChangesAsync(cancellationToken);
        }

        var taken = await Service().EnsureAsync(squatted.Id, cancellationToken);
        var missing = await Service().EnsureAsync(Guid.NewGuid(), cancellationToken);

        Assert.False(taken.IsSuccess);
        Assert.False(missing.IsSuccess);
        await using (var check = this.database.CreateSystemContext())
        {
            var squatter = await check.Users.SingleAsync(user => user.Id == SystemAccount.IdFor(squatted.Id), cancellationToken);
            Assert.False(squatter.IsSystemAccount);
        }

        await UserEdits.DeleteInTableAsync(this.database, SystemAccount.IdFor(squatted.Id), cancellationToken);

        Assert.True((await Service().EnsureAsync(squatted.Id, cancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task In_system_scope_a_row_stamped_with_its_own_tenants_system_account_is_accepted_and_with_another_tenants_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        Guid accepted;

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = new TestParent { Name = "On A's behalf", TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId };
            system.TestParents.Add(parent);
            await system.SaveChangesAsync(cancellationToken);
            accepted = parent.Id;
        }

        await using (var system = this.database.CreateContext(scenario.B.Runtime(), new FakeClock(noon)))
        using (system.BeginSystemScope("test: edit on A's behalf"))
        {
            var parent = await system.TestParents.SingleAsync(row => row.Id == accepted, cancellationToken);
            parent.Notes = "Edited on A's behalf";
            parent.ModifiedById = scenario.A.SystemAccountId;
            await system.SaveChangesAsync(cancellationToken);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.TestParents.Add(new TestParent { Name = "Stamped with B's account", TenantId = scenario.A.TenantId, CreatedById = scenario.B.SystemAccountId });
            var refusal = await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Contains("not a user of the row's tenant", refusal.Message);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = await system.TestParents.SingleAsync(row => row.Id == accepted, cancellationToken);
            parent.Notes = "Edited with B's account";
            parent.ModifiedById = scenario.B.SystemAccountId;
            var refusal = await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Contains("not a user of the row's tenant", refusal.Message);
        }

        await using var check = this.database.CreateSystemContext();
        var rows = await check.TestParents.AsNoTracking().Where(row => row.TenantId == scenario.A.TenantId).ToListAsync(cancellationToken);
        var stored = Assert.Single(rows);
        Assert.Equal((scenario.A.SystemAccountId, scenario.A.SystemAccountId, noon.UtcDateTime), (stored.CreatedById, stored.ModifiedById, stored.Modified));
        Assert.Equal("Edited on A's behalf", stored.Notes);
        Assert.Null(stored.ImpersonatedById);
    }

    [Fact]
    public async Task Startup_logs_a_tenant_whose_account_cannot_be_ensured_and_still_ensures_every_other_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        var squatted = new Tenant { Name = "Company E", State = TenantState.Active };
        var bare = new Tenant { Name = "Company F", State = TenantState.Active };
        await using (var context = this.database.CreateSystemContext())
        {
            context.Tenants.AddRange(squatted, bare);
            await context.SaveChangesAsync(cancellationToken);
            context.Users.Add(new ApplicationUser { Id = SystemAccount.IdFor(squatted.Id), UserName = "squatter.e", NormalizedUserName = "SQUATTER.E", TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId });
            await context.SaveChangesAsync(cancellationToken);
        }

        var log = new RecordingLogger<SystemAccountService>();
        var summary = await Service(log).EnsureForEveryTenantAsync(cancellationToken);

        Assert.True(summary.IsSuccess, summary.Error);
        Assert.Equal([squatted.Id], summary.Value!.FailedTenantIds);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains(squatted.Id.ToString()));
        Assert.Single(await AccountsOfAsync([bare.Id], cancellationToken));
        Assert.Empty(await AccountsOfAsync([squatted.Id], cancellationToken));

        await UserEdits.DeleteInTableAsync(this.database, SystemAccount.IdFor(squatted.Id), cancellationToken);

        var again = await Service().EnsureForEveryTenantAsync(cancellationToken);
        Assert.Empty(again.Value!.FailedTenantIds);
        Assert.Single(await AccountsOfAsync([squatted.Id], cancellationToken));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("email")]
    [InlineData("user name")]
    public async Task A_flagged_row_holding_the_derived_id_that_was_given_a_password_an_address_or_another_name_is_not_accepted_as_the_account(string change)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Company G", cancellationToken);

        await using (var context = this.database.CreateSystemContext())
        {
            var account = await context.Users.SingleAsync(user => user.Id == tenant.SystemAccountId, cancellationToken);
            context.NameEditor(account, tenant.SystemAccountId);
            switch (change)
            {
                case "password":
                    account.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(account, "Password123!");
                    break;
                case "email":
                    account.Email = "taken.over@company-g.example";
                    account.NormalizedEmail = account.Email.ToUpperInvariant();
                    break;
                default:
                    account.UserName = "taken.over";
                    account.NormalizedUserName = "TAKEN.OVER";
                    break;
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        var log = new RecordingLogger<SystemAccountService>();
        var ensured = await Service(log).EnsureAsync(tenant.TenantId, cancellationToken);

        Assert.False(ensured.IsSuccess);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains(tenant.TenantId.ToString()));
        await using (var context = this.database.CreateSystemContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SystemAccountService.EnsureAsync(context, tenant.TenantId, cancellationToken));

            var userName = SystemAccount.UserNameFor(tenant.TenantId);
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET PasswordHash = NULL, Email = NULL, NormalizedEmail = NULL, UserName = {userName}, NormalizedUserName = {userName.ToUpperInvariant()} WHERE Id = {tenant.SystemAccountId}", cancellationToken);
        }

        Assert.True((await Service().EnsureAsync(tenant.TenantId, cancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task In_system_scope_a_flagged_user_of_the_rows_tenant_without_the_derived_id_is_refused_as_creator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        var impostor = new ApplicationUser { Id = Guid.NewGuid(), UserName = "impostor.a", NormalizedUserName = "IMPOSTOR.A", TenantId = scenario.A.TenantId, IsSystemAccount = true, CreatedById = scenario.A.OwnerId };
        await using (var context = this.database.CreateSystemContext())
        {
            context.Users.Add(impostor);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.TestParents.Add(new TestParent { Name = "Stamped with an impostor", TenantId = scenario.A.TenantId, CreatedById = impostor.Id });
            var refusal = await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Contains("not a user of the row's tenant", refusal.Message);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.TestParents.AnyAsync(row => row.CreatedById == impostor.Id, cancellationToken));
    }

    private SystemAccountService Service(ILogger<SystemAccountService>? logger = null) =>
        new(new ApplicationDbContextFactory(new TestContexts(this.database), TimeProvider.System), logger ?? NullLogger<SystemAccountService>.Instance);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static List<string> Snapshot(IEnumerable<ApplicationUser> accounts) =>
        [.. accounts.Select(account => $"{account.Id} {account.TenantId} {account.UserName} {account.NormalizedUserName} {account.FirstName} {account.LastName} {account.State} {account.IsSystemAccount} {account.PasswordHash} {account.SecurityStamp} {account.ConcurrencyStamp}")];

    private async Task<List<ApplicationUser>> AccountsOfAsync(Guid[] tenantIds, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.AsNoTracking()
            .Where(user => user.IsSystemAccount && tenantIds.Contains(user.TenantId))
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);
    }

    private sealed class TestContexts : IDbContextFactory<ApplicationDbContext>
    {
        private readonly RealDatabase database;

        public TestContexts(RealDatabase database)
        {
            this.database = database;
        }

        public ApplicationDbContext CreateDbContext() => new TestDataContext(ApplicationDbContextOptions.Create(this.database.ConnectionString));
    }
}
