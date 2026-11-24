using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Settings;
using Staffora.Services.Settings;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class TenantSettingsTests : IClassFixture<RealDatabase>
{
    private const string SettingsTable = "[TenantSettings]";
    private static readonly string key = TenantSettingDefinitions.TimeZone.Key;

    private readonly RealDatabase database;

    public TenantSettingsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Another_circuit_reads_the_time_zone_an_administrator_changed_on_its_next_load_through_the_invalidated_cache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, reader) = await ArrangeAsync(cancellationToken);
        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        await using var readerCircuit = ServiceHost.Create(this.database, scenario.A.RuntimeOf(reader), ServiceHost.SharingLookupCacheOf(app));
        await using var adminCircuit = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator), ServiceHost.SharingLookupCacheOf(app));
        var clock = readerCircuit.Get<TenantClock>();

        Assert.True((await app.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.A.TenantId, cancellationToken)).Value);
        ClearLogs(app, readerCircuit, adminCircuit);

        Assert.Equal(ServiceHost.PlatformTimeZone, (await clock.GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(1, Queries(app, readerCircuit, adminCircuit));
        Assert.Equal(ServiceHost.PlatformTimeZone, (await clock.GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(1, Queries(app, readerCircuit, adminCircuit));

        var settings = adminCircuit.Get<TenantSettingsService>();
        var loaded = (await settings.LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.True((await settings.SaveAsync(key, "America/New_York", loaded.Version, cancellationToken)).IsSuccess);
        ClearLogs(app, readerCircuit, adminCircuit);

        Assert.Equal("America/New_York", (await clock.GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(1, Queries(app, readerCircuit, adminCircuit));
        Assert.Equal(TimeSpan.FromHours(-5), TenantClock.ToLocal(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), (await clock.GetTimeZoneAsync(cancellationToken)).Value!).Offset);
        Assert.Equal("America/New_York", (await clock.GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(1, Queries(app, readerCircuit, adminCircuit));
    }

    [Fact]
    public async Task A_user_without_SettingsEdit_is_refused_by_the_service_and_one_without_SettingsView_reads_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, reader) = await ArrangeAsync(cancellationToken);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.SettingsView);

        await using (var viewerCircuit = ServiceHost.Create(this.database, scenario.A.RuntimeOf(viewer)))
        {
            var settings = viewerCircuit.Get<TenantSettingsService>();
            var snapshot = (await settings.LoadAsync(cancellationToken)).Value!;
            Assert.False(snapshot.CanEdit);
            Assert.Equal(TenantSettingsService.NoEditPermission, (await settings.SaveAsync(key, "Asia/Tokyo", snapshot.Of(TenantSettingDefinitions.TimeZone).Version, cancellationToken)).Error);
            Assert.Equal(TenantSettingsService.NoEditPermission, (await settings.SaveAsync(key, ServiceHost.PlatformTimeZone, null, cancellationToken)).Error);
        }

        await using (var readerCircuit = ServiceHost.Create(this.database, scenario.A.RuntimeOf(reader)))
        {
            Assert.Equal(TenantSettingsService.NoViewPermission, (await readerCircuit.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Error);
            Assert.Equal(TenantSettingsService.NoEditPermission, (await readerCircuit.Get<TenantSettingsService>().SaveAsync(key, "Asia/Tokyo", null, cancellationToken)).Error);

            Assert.Equal(ServiceHost.PlatformTimeZone, (await readerCircuit.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        }

        Assert.Empty(await RowsAsync(scenario.A.TenantId, cancellationToken));
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("europe/stockholm")]
    public async Task An_unknown_time_zone_is_refused_with_its_reason(string zone)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator));

        var refused = await host.Get<TenantSettingsService>().SaveAsync(key, zone, null, cancellationToken);

        Assert.Equal($"\"{zone}\" is not a time zone this system offers. Choose one from the list, such as Europe/Stockholm.", refused.Error);
        Assert.Equal(TenantSettingsService.UnknownSetting, (await host.Get<TenantSettingsService>().SaveAsync("General.Colour", "Blue", null, cancellationToken)).Error);
        Assert.Empty(await RowsAsync(scenario.A.TenantId, cancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_value_is_refused_and_the_row_stays(string? value)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator));
        Assert.True((await host.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.A.TenantId, cancellationToken)).Value);
        var before = Assert.Single(await RowsAsync(scenario.A.TenantId, cancellationToken));
        var loaded = (await host.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);

        Assert.Equal("Time zone needs a value.", (await host.Get<TenantSettingsService>().SaveAsync(key, value, loaded.Version, cancellationToken)).Error);

        var after = Assert.Single(await RowsAsync(scenario.A.TenantId, cancellationToken));
        Assert.Equal((before.Id, before.Value, Convert.ToHexString(before.RowVersion)), (after.Id, after.Value, Convert.ToHexString(after.RowVersion)));
    }

    [Fact]
    public async Task A_save_over_a_change_made_meanwhile_is_refused_and_the_platform_zone_is_stored_as_the_tenants_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        await using var first = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator), ServiceHost.SharingLookupCacheOf(app));
        await using var second = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague), ServiceHost.SharingLookupCacheOf(app));
        Assert.True((await app.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.A.TenantId, cancellationToken)).Value);

        var openedByFirst = (await first.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        var openedBySecond = (await second.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.Equal((ServiceHost.PlatformTimeZone, ServiceHost.PlatformTimeZone, ServiceHost.PlatformTimeZone, TenantSettingSource.Stored), (openedByFirst.Value, openedByFirst.StoredValue, openedByFirst.DefaultValue, openedByFirst.Source));
        Assert.NotNull(openedByFirst.Version);
        Assert.True((await first.Get<TenantSettingsService>().SaveAsync(key, "Asia/Tokyo", openedByFirst.Version, cancellationToken)).IsSuccess);
        Assert.Equal(TenantSettingsService.ChangedMeanwhile, (await second.Get<TenantSettingsService>().SaveAsync(key, "Europe/London", openedBySecond.Version, cancellationToken)).Error);

        var reopened = (await second.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.Equal(("Asia/Tokyo", TenantSettingSource.Stored), (reopened.Value, reopened.Source));
        Assert.True((await second.Get<TenantSettingsService>().SaveAsync(key, "Europe/London", reopened.Version, cancellationToken)).IsSuccess);
        Assert.Equal(TenantSettingsService.ChangedMeanwhile, (await first.Get<TenantSettingsService>().SaveAsync(key, "Asia/Seoul", reopened.Version, cancellationToken)).Error);
        Assert.Equal(["Europe/London"], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));

        var current = (await first.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.True((await first.Get<TenantSettingsService>().SaveAsync(key, current.DefaultValue, current.Version, cancellationToken)).IsSuccess);
        Assert.Equal([ServiceHost.PlatformTimeZone], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
        Assert.Equal(ServiceHost.PlatformTimeZone, (await second.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        var back = (await second.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.Equal((ServiceHost.PlatformTimeZone, TenantSettingSource.Stored), (back.StoredValue, back.Source));
    }

    [Fact]
    public async Task A_refused_save_drops_the_stale_entry_so_the_settings_opened_again_save()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);

        await using var instanceOne = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator));
        await using var instanceTwo = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator));
        var one = instanceOne.Get<TenantSettingsService>();
        var two = instanceTwo.Get<TenantSettingsService>();
        Assert.True((await one.SaveAsync(key, "Asia/Tokyo", null, cancellationToken)).IsSuccess);
        var openedOnOne = (await one.LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);

        var openedOnTwo = (await two.LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.True((await two.SaveAsync(key, "Europe/London", openedOnTwo.Version, cancellationToken)).IsSuccess);
        Assert.Equal(TenantSettingsService.ChangedMeanwhile, (await one.SaveAsync(key, "America/New_York", openedOnOne.Version, cancellationToken)).Error);

        var reopened = (await one.LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.Equal("Europe/London", reopened.Value);
        Assert.NotEqual(openedOnOne.Version, reopened.Version);
        Assert.True((await one.SaveAsync(key, "America/New_York", reopened.Version, cancellationToken)).IsSuccess);
        Assert.Equal("America/New_York", (await instanceOne.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(["America/New_York"], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
    }

    [Fact]
    public async Task Two_first_saves_at_once_store_one_row_and_the_other_is_refused_by_the_unique_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);

            var meeting = new SaveMeeting(participants: 2);
            await using var left = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator), interceptors: [meeting]);
            await using var right = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator), interceptors: [meeting]);
            var results = await Task.WhenAll(
                left.Get<TenantSettingsService>().SaveAsync(key, "Asia/Tokyo", null, cancellationToken),
                right.Get<TenantSettingsService>().SaveAsync(key, "Europe/London", null, cancellationToken));

            Assert.True(meeting.Met, "both saves reached SaveChanges together");
            Assert.Single(results, result => result.IsSuccess);
            Assert.Single(results, result => result.Error == TenantSettingsService.ChangedMeanwhile);
            Assert.Single(await RowsAsync(scenario.A.TenantId, cancellationToken));
        }
    }

    [Fact]
    public async Task A_row_changed_between_the_read_and_the_save_is_refused_by_its_row_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);
        await using (var pin = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            Assert.True((await pin.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.A.TenantId, cancellationToken)).Value);
        }

        var meddler = new ChangeRowBeforeSave(this.database.ConnectionString, scenario.A.TenantId, key, "Asia/Seoul");
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator), interceptors: [meddler]);
        var loaded = (await host.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);

        var refused = await host.Get<TenantSettingsService>().SaveAsync(key, "Asia/Tokyo", loaded.Version, cancellationToken);

        Assert.True(meddler.Changed);
        Assert.Equal(TenantSettingsService.ChangedMeanwhile, refused.Error);
        Assert.Equal(["Asia/Seoul"], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
    }

    [Fact]
    public async Task Settings_never_cross_tenants_in_rows_or_cache_entries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        await using var ofA = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator), ServiceHost.SharingLookupCacheOf(app));
        await using var ofB = ServiceHost.Create(this.database, scenario.B.Runtime(), ServiceHost.SharingLookupCacheOf(app));

        Assert.True((await ofA.Get<TenantSettingsService>().SaveAsync(key, "America/New_York", null, cancellationToken)).IsSuccess);
        Assert.Equal("America/New_York", (await ofA.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(ServiceHost.PlatformTimeZone, (await ofB.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(TenantSettingSource.Missing, (await ofB.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone).Source);

        Assert.True((await ofB.Get<TenantSettingsService>().SaveAsync(key, "Asia/Tokyo", null, cancellationToken)).IsSuccess);
        ClearLogs(app, ofA, ofB);
        Assert.Equal("America/New_York", (await ofA.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        Assert.Equal(0, Queries(app, ofA, ofB));
        Assert.Equal("Asia/Tokyo", (await ofB.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);

        Assert.Equal([("America/New_York", administrator)], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => (row.Value, row.CreatedById)));
        Assert.Equal([("Asia/Tokyo", scenario.B.OwnerId)], (await RowsAsync(scenario.B.TenantId, cancellationToken)).Select(row => (row.Value, row.CreatedById)));

        await using var contextOfA = this.database.CreateContext(scenario.A.RuntimeOf(administrator));
        Assert.Equal([scenario.A.TenantId], await contextOfA.TenantSettings.Select(row => row.TenantId).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task The_platform_console_is_refused_and_an_impersonating_owner_saves_the_tenants_row_stamped_with_the_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var settings = console.Get<TenantSettingsService>();
            Assert.Equal(TenantSettingsService.PlatformConsoleRefused, (await settings.LoadAsync(cancellationToken)).Error);
            Assert.Equal(TenantSettingsService.PlatformConsoleRefused, (await settings.SaveAsync(key, "Asia/Tokyo", null, cancellationToken)).Error);

            Assert.Equal(ServiceHost.PlatformTimeZone, (await console.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
            Assert.DoesNotContain(console.CommandLog, command => command.Contains(SettingsTable, StringComparison.Ordinal));
        }

        Assert.Empty(await RowsAsync(platform.TenantId, cancellationToken));
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(administrator, scenario.A.TenantId);
        await using (var asAdministrator = ServiceHost.Create(this.database, impersonating))
        {
            var settings = asAdministrator.Get<TenantSettingsService>();
            Assert.True((await settings.LoadAsync(cancellationToken)).Value!.CanEdit);
            Assert.True((await settings.SaveAsync(key, "Asia/Tokyo", null, cancellationToken)).IsSuccess);
            Assert.Equal("Asia/Tokyo", (await asAdministrator.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        }

        var row = Assert.Single(await RowsAsync(scenario.A.TenantId, cancellationToken));
        Assert.Equal(("Asia/Tokyo", administrator, (Guid?)platform.OwnerId), (row.Value, row.CreatedById, row.ImpersonatedById));
        Assert.Empty(await RowsAsync(platform.TenantId, cancellationToken));
    }

    [Fact]
    public async Task A_stored_zone_the_system_does_not_accept_reads_as_the_platforms_and_can_be_replaced_or_set_to_the_platforms()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, administrator, _) = await ArrangeAsync(cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            system.TenantSettings.Add(new TenantSetting { TenantId = scenario.A.TenantId, Key = key, Value = "Asia/Calcutta", CreatedById = scenario.A.SystemAccountId });
            await system.SaveChangesAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator));
        Assert.Equal(ServiceHost.PlatformTimeZone, (await host.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);
        var state = (await host.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.Equal((ServiceHost.PlatformTimeZone, "Asia/Calcutta", TenantSettingSource.NotAccepted), (state.Value, state.StoredValue, state.Source));
        Assert.NotNull(state.Version);

        Assert.True((await host.Get<TenantSettingsService>().SaveAsync(key, "Asia/Kolkata", state.Version, cancellationToken)).IsSuccess);
        Assert.Equal(["Asia/Kolkata"], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
        Assert.Equal("Asia/Kolkata", (await host.Get<TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!.Id);

        var replaced = (await host.Get<TenantSettingsService>().LoadAsync(cancellationToken)).Value!.Of(TenantSettingDefinitions.TimeZone);
        Assert.True((await host.Get<TenantSettingsService>().SaveAsync(key, replaced.DefaultValue, replaced.Version, cancellationToken)).IsSuccess);
        Assert.Equal([ServiceHost.PlatformTimeZone], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
    }

    private async Task<(TwoTenantScenario Scenario, Guid Administrator, Guid Reader)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Administrator", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, administrator, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        var reader = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Reader", cancellationToken);
        return (scenario, administrator, reader);
    }

    private async Task<List<TenantSetting>> RowsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TenantSettings.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.Key).ToListAsync(cancellationToken);
    }

    private static int Queries(params ServiceHost[] hosts) =>
        hosts.SelectMany(host => host.CommandLog).Count(command => command.Contains(SettingsTable, StringComparison.Ordinal));

    private static void ClearLogs(params ServiceHost[] hosts)
    {
        foreach (var host in hosts)
        {
            host.ClearCommandLog();
        }
    }

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

    private sealed class ChangeRowBeforeSave : SaveChangesInterceptor
    {
        private readonly string connectionString;
        private readonly Guid tenantId;
        private readonly string key;
        private readonly string value;

        public ChangeRowBeforeSave(string connectionString, Guid tenantId, string key, string value)
        {
            this.connectionString = connectionString;
            this.tenantId = tenantId;
            this.key = key;
            this.value = value;
        }

        public bool Changed { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Changed)
            {
                await using var connection = new SqlConnection(this.connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE [TenantSettings] SET [Value] = @value WHERE [TenantId] = @tenantId AND [Key] = @key";
                command.Parameters.AddWithValue("@value", this.value);
                command.Parameters.AddWithValue("@tenantId", this.tenantId);
                command.Parameters.AddWithValue("@key", this.key);
                Changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            }

            return result;
        }
    }
}
