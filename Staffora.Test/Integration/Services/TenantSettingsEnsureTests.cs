using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Settings;
using Staffora.Services.Settings;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class TenantSettingsEnsureTests : IClassFixture<RealDatabase>
{
    private static readonly string key = TenantSettingDefinitions.TimeZone.Key;

    private readonly RealDatabase database;

    public TenantSettingsEnsureTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Startup_pins_the_platform_zone_for_every_tenant_without_one_and_never_touches_a_row_a_tenant_holds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var unaccepted = await TwoTenantScenario.AddTenantAsync(this.database, "Company C", cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await AddRowAsync(scenario.A, "Asia/Tokyo", cancellationToken);
        await AddRowAsync(unaccepted, "Asia/Calcutta", cancellationToken);
        var heldBefore = await RowsAsync([scenario.A.TenantId, unaccepted.TenantId], cancellationToken);

        await using var one = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        await using var two = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var runs = await Task.WhenAll(
            one.Get<TenantSettingsEnsureService>().EnsureTimeZoneForEveryTenantAsync(cancellationToken),
            two.Get<TenantSettingsEnsureService>().EnsureTimeZoneForEveryTenantAsync(cancellationToken));

        Assert.All(runs, run => Assert.True(run.IsSuccess, run.Error));
        Assert.All(runs, run => Assert.Empty(run.Value!.FailedTenantIds));
        Assert.True(runs.Sum(run => run.Value!.Created) >= 1);

        var pinned = Assert.Single(await RowsAsync([scenario.B.TenantId], cancellationToken));
        Assert.Equal((ServiceHost.PlatformTimeZone, SystemAccount.IdFor(scenario.B.TenantId), (Guid?)null), (pinned.Value, pinned.CreatedById, pinned.ImpersonatedById));

        Assert.Equal(Snapshot(heldBefore), Snapshot(await RowsAsync([scenario.A.TenantId, unaccepted.TenantId], cancellationToken)));
        Assert.Empty(await RowsAsync([platform.TenantId], cancellationToken));

        var again = await one.Get<TenantSettingsEnsureService>().EnsureTimeZoneForEveryTenantAsync(cancellationToken);
        Assert.Equal((0, 0), (again.Value!.Created, again.Value.FailedTenantIds.Count));
        Assert.Single(await RowsAsync([scenario.B.TenantId], cancellationToken));
    }

    [Fact]
    public async Task Two_ensures_of_one_tenant_at_once_write_one_row_and_both_succeed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

            var meeting = new SaveMeeting(participants: 2);
            await using var one = ServiceHost.Create(this.database, new RuntimeExecutionContext(), interceptors: [meeting]);
            await using var two = ServiceHost.Create(this.database, new RuntimeExecutionContext(), interceptors: [meeting]);
            var results = await Task.WhenAll(
                one.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.A.TenantId, cancellationToken),
                two.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.A.TenantId, cancellationToken));

            Assert.True(meeting.Met, "both ensures reached SaveChanges together");
            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error));
            Assert.Equal([false, true], results.Select(result => result.Value).Order());
            Assert.Equal([ServiceHost.PlatformTimeZone], (await RowsAsync([scenario.A.TenantId], cancellationToken)).Select(row => row.Value));
        }
    }

    [Fact]
    public async Task The_apps_startup_pins_every_tenant_to_the_zone_it_is_configured_with()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await AddRowAsync(scenario.A, "Asia/Tokyo", cancellationToken);

        await using (AppHost.Start(this.database.ConnectionString, "Test", new Dictionary<string, string> { ["Platform:TimeZone"] = "America/New_York" }))
        {
        }

        Assert.Equal([("Asia/Tokyo", scenario.A.TenantId), ("America/New_York", scenario.B.TenantId)], (await RowsAsync([scenario.A.TenantId, scenario.B.TenantId], cancellationToken)).OrderBy(row => row.Value != "Asia/Tokyo").Select(row => (row.Value, row.TenantId)));
        Assert.Equal(SystemAccount.IdFor(scenario.B.TenantId), (await RowsAsync([scenario.B.TenantId], cancellationToken)).Single().CreatedById);
    }

    [Fact]
    public async Task A_tenant_with_a_row_keeps_its_zone_when_the_platforms_changes_while_the_console_and_a_tenant_without_one_follow_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.SettingsView);
        await using (var startedInStockholm = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            Assert.True((await startedInStockholm.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.B.TenantId, cancellationToken)).Value);
        }

        var warnings = new WarningLog();
        void InTokyo(IServiceCollection services)
        {
            services.Configure<PlatformOptions>(options => options.TimeZone = "Asia/Tokyo");
            services.AddSingleton<ILoggerProvider>(warnings);
        }

        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext(), InTokyo);
        await using (var ofB = ServiceHost.Create(this.database, scenario.B.Runtime(), services => { InTokyo(services); ServiceHost.SharingLookupCacheOf(app)(services); }))
        {
            Assert.Equal(ServiceHost.PlatformTimeZone, (await ofB.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
            var state = (await ofB.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
            Assert.Equal((ServiceHost.PlatformTimeZone, "Asia/Tokyo", TenantSettingSource.Stored), (state.Value, state.DefaultValue, state.Source));
        }

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), InTokyo))
        {
            Assert.Equal("Asia/Tokyo", (await console.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        }

        var another = await TwoTenantScenario.AddTenantAsync(this.database, "Company D", cancellationToken);
        foreach (var tenant in new[] { another, scenario.A })
        {
            await using var circuit = ServiceHost.Create(this.database, tenant.Runtime(), services => { InTokyo(services); ServiceHost.SharingLookupCacheOf(app)(services); });
            Assert.Equal("Asia/Tokyo", (await circuit.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
            Assert.Equal("Asia/Tokyo", (await circuit.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        }

        Assert.Single(warnings.Messages, message => message.Contains(another.TenantId.ToString(), StringComparison.Ordinal));
        var missing = Assert.Single(warnings.Messages, message => message.Contains(scenario.A.TenantId.ToString(), StringComparison.Ordinal));
        Assert.Contains("holds no value of General.TimeZone", missing, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings.Messages, message => message.Contains(scenario.B.TenantId.ToString(), StringComparison.Ordinal) || message.Contains(platform.TenantId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pinning_the_zone_of_a_tenant_that_does_not_exist_fails_and_writes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var nobody = Guid.NewGuid();

        var refused = await host.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(nobody, cancellationToken);

        Assert.Equal("The tenant's time zone could not be pinned.", refused.Error);
        Assert.Empty(await RowsAsync([nobody], cancellationToken));
    }

    private async Task AddRowAsync(ScenarioTenant tenant, string value, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        system.TenantSettings.Add(new TenantSetting { TenantId = tenant.TenantId, Key = key, Value = value, CreatedById = tenant.SystemAccountId });
        await system.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<TenantSetting>> RowsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TenantSettings.AsNoTracking().Where(row => tenantIds.Contains(row.TenantId)).OrderBy(row => row.TenantId).ThenBy(row => row.Key).ToListAsync(cancellationToken);
    }

    private static List<(Guid Id, Guid TenantId, string Value, string RowVersion, Guid CreatedById)> Snapshot(IEnumerable<TenantSetting> rows) =>
        [.. rows.Select(row => (row.Id, row.TenantId, row.Value, Convert.ToHexString(row.RowVersion), row.CreatedById))];

    private sealed class SaveMeeting : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;
        private readonly int participants;

        public SaveMeeting(int participants)
        {
            this.participants = participants;
        }

        public bool Met => this.all.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref this.arrived) == this.participants)
            {
                this.all.TrySetResult();
            }

            await this.all.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> messages = new();

        public IReadOnlyList<string> Messages => [.. this.messages];

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(TenantSettingsService).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                this.messages.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
