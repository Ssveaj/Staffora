using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.EntityAttributes;
using Staffora.Blazor.Components.Settings;
using Staffora.Blazor.Pages.TenantAdmin;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Settings;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Settings;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class SettingsPageTests : IClassFixture<RealDatabase>
{
    private static readonly DateTimeOffset now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private const string Stockholm = "(UTC+01:00) Europe/Stockholm";
    private const string UsePlatformZone = "Use the platform's time zone (Europe/Stockholm)";

    private readonly RealDatabase database;

    public SettingsPageTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task An_administrator_changes_the_time_zone_and_returns_it_to_the_platforms_in_the_general_panel()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId));
        Assert.True((await circuit.Get<TenantSettingsEnsureService>().EnsureTimeZoneAsync(scenario.A.TenantId, cancellationToken)).Value);
        using var page = circuit.Page("/tenant/management/settings");
        var rendered = page.Render(builder => Host<Settings>(builder));
        var panel = PanelOf(rendered);

        Assert.Equal("page", rendered.Find("nav[aria-label='Settings'] a[href='/tenant/management/settings']").GetAttribute("aria-current"));
        Assert.Null(rendered.Find("nav[aria-label='Settings'] a[href='/tenant/management/settings/custom-fields']").GetAttribute("aria-current"));
        Assert.Equal("General", rendered.Find("h2.page-section__title").TextContent.Trim());
        var picker = panel.FindComponent<RadzenDropDown<string>>();
        Assert.Equal(ServiceHost.PlatformTimeZone, picker.Instance.Value);
        Assert.False(picker.Instance.Disabled);
        Assert.Empty(panel.FindAll(".general-settings__fallback"));
        Assert.Equal(Stockholm, Fact(panel, "Platform's time zone"));
        Assert.Equal("Tuesday 3 November 2026, 13:00", Fact(panel, "Time now in this zone"));
        Assert.True(Button(panel, "Save").HasAttribute("disabled"));
        Assert.DoesNotContain(panel.FindAll("button"), button => ButtonText(button) == UsePlatformZone);

        await rendered.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync("America/New_York"));
        Assert.Equal("Tuesday 3 November 2026, 07:00", Fact(panel, "Time now in this zone"));
        await rendered.InvokeAsync(() => Button(panel, "Save").Click());
        rendered.WaitForAssertion(() => Assert.Equal("Saved: the company's time zone is now (UTC-05:00) America/New York.", Status(panel)), TimeSpan.FromSeconds(10));
        Assert.Equal([("America/New_York", scenario.A.OwnerId)], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => (row.Value, row.ModifiedById ?? row.CreatedById)));
        Assert.Equal("America/New_York", panel.FindComponent<RadzenDropDown<string>>().Instance.Value);
        Assert.True(Button(panel, "Save").HasAttribute("disabled"));

        await rendered.InvokeAsync(() => Button(panel, UsePlatformZone).Click());
        rendered.WaitForAssertion(() => Assert.Equal($"Saved: the company's time zone is now the platform's, {Stockholm}.", Status(panel)), TimeSpan.FromSeconds(10));
        Assert.Equal([ServiceHost.PlatformTimeZone], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
        Assert.Equal(ServiceHost.PlatformTimeZone, panel.FindComponent<RadzenDropDown<string>>().Instance.Value);
        Assert.DoesNotContain(panel.FindAll("button"), button => ButtonText(button) == UsePlatformZone);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task An_administrator_closes_the_company_on_its_public_holidays_and_then_chooses_no_calendar_in_the_general_panel()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId));
        using var page = circuit.Page("/tenant/management/settings");
        var rendered = page.Render(builder => Host<Settings>(builder));
        var panel = PanelOf(rendered);

        var section = panel.Find("[data-setting-public-holidays]");
        Assert.Equal("Public holidays", section.QuerySelector("h3")!.TextContent.Trim());
        var calendar = panel.FindComponents<RadzenDropDown<string>>().Single(dropDown => dropDown.Instance.Name == "settings-public-holidays");
        Assert.Equal("SE", calendar.Instance.Value);
        Assert.False(panel.FindComponent<RadzenSwitch>().Instance.Value);
        Assert.Equal("Christmas Eve, Thursday 24 December 2026", Fact(panel, "Next public holiday"));
        Assert.True(Button(panel, "Save public holidays").HasAttribute("disabled"));

        await rendered.InvokeAsync(() => panel.FindComponent<RadzenSwitch>().Instance.ValueChanged.InvokeAsync(true));
        await rendered.InvokeAsync(() => Button(panel, "Save public holidays").Click());
        rendered.WaitForAssertion(() => Assert.Equal("Saved: the public holidays of Sweden are marked and block staffing.", Status(panel)), TimeSpan.FromSeconds(10));
        Assert.Equal([("General.ClosedOnPublicHolidays", "true")], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Where(row => row.Key.Contains("Holidays", StringComparison.Ordinal)).Select(row => (row.Key, row.Value)));
        Assert.True(panel.FindComponent<RadzenSwitch>().Instance.Value);
        Assert.True(Button(panel, "Save public holidays").HasAttribute("disabled"));

        calendar = panel.FindComponents<RadzenDropDown<string>>().Single(dropDown => dropDown.Instance.Name == "settings-public-holidays");
        await rendered.InvokeAsync(() => calendar.Instance.ValueChanged.InvokeAsync(string.Empty));
        Assert.Equal("None, no calendar is chosen", Fact(panel, "Next public holiday"));
        await rendered.InvokeAsync(() => Button(panel, "Save public holidays").Click());
        rendered.WaitForAssertion(() => Assert.Equal("Saved: no public holidays are marked.", Status(panel)), TimeSpan.FromSeconds(10));
        Assert.Equal([("General.ClosedOnPublicHolidays", "true"), ("General.PublicHolidays", string.Empty)], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Where(row => row.Key.Contains("Holidays", StringComparison.Ordinal)).Select(row => (row.Key, row.Value)));
        var holidays = await circuit.Get<TenantClock>().GetHolidaysAsync(cancellationToken);
        Assert.Equal((true, false, true), (holidays.Value!.Calendar.IsNone, holidays.Value.Blocks, holidays.Value.ClosedOnHolidays));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_stored_zone_this_server_does_not_know_is_said_so_and_replaced_by_the_platforms_or_another()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        foreach (var tenant in new[] { scenario.A, scenario.B })
        {
            await TwoTenantScenario.GrantAsync(this.database, tenant, tenant.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
            await AddRowAsync(tenant, "Mars/Olympus_Mons", cancellationToken);
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId)))
        {
            using var page = circuit.Page("/tenant/management/settings");
            var rendered = page.Render(builder => Host<Settings>(builder));
            var panel = PanelOf(rendered);

            Assert.Equal($"The company's saved time zone, Mars/Olympus_Mons, is not known on this server. Choose another. Until then, times are shown in the platform's time zone, {Stockholm}.", Notice(panel));
            Assert.Equal(ServiceHost.PlatformTimeZone, panel.FindComponent<RadzenDropDown<string>>().Instance.Value);
            Assert.False(Button(panel, "Save").HasAttribute("disabled"));
            await rendered.InvokeAsync(() => Button(panel, UsePlatformZone).Click());
            rendered.WaitForAssertion(() => Assert.Equal($"Saved: the company's time zone is now the platform's, {Stockholm}.", Status(panel)), TimeSpan.FromSeconds(10));
            Assert.Empty(panel.FindAll(".general-settings__fallback"));
            Assert.Equal([ServiceHost.PlatformTimeZone], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.B.OwnerId)))
        {
            using var page = circuit.Page("/tenant/management/settings");
            var rendered = page.Render(builder => Host<Settings>(builder));
            var panel = PanelOf(rendered);

            Assert.StartsWith("The company's saved time zone, Mars/Olympus_Mons, is not known on this server", Notice(panel), StringComparison.Ordinal);
            await rendered.InvokeAsync(() => panel.FindComponent<RadzenDropDown<string>>().Instance.ValueChanged.InvokeAsync("Asia/Tokyo"));
            await rendered.InvokeAsync(() => Button(panel, "Save").Click());
            rendered.WaitForAssertion(() => Assert.Equal("Saved: the company's time zone is now (UTC+09:00) Asia/Tokyo.", Status(panel)), TimeSpan.FromSeconds(10));
            Assert.Empty(panel.FindAll(".general-settings__fallback"));
            Assert.Equal(["Asia/Tokyo"], (await RowsAsync(scenario.B.TenantId, cancellationToken)).Select(row => row.Value));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_company_without_a_zone_of_its_own_is_said_so_and_one_save_stores_the_platforms()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId));
        using var page = circuit.Page("/tenant/management/settings");
        var rendered = page.Render(builder => Host<Settings>(builder));
        var panel = PanelOf(rendered);

        Assert.Equal($"The company has no time zone of its own saved, so times are shown in the platform's time zone, {Stockholm}. Choose a time zone and save it.", Notice(panel));
        Assert.Equal(ServiceHost.PlatformTimeZone, panel.FindComponent<RadzenDropDown<string>>().Instance.Value);
        Assert.False(Button(panel, "Save").HasAttribute("disabled"));
        await rendered.InvokeAsync(() => Button(panel, "Save").Click());
        rendered.WaitForAssertion(() => Assert.Equal($"Saved: the company's time zone is now {Stockholm}.", Status(panel)), TimeSpan.FromSeconds(10));
        Assert.Empty(panel.FindAll(".general-settings__fallback"));
        Assert.Equal([ServiceHost.PlatformTimeZone], (await RowsAsync(scenario.A.TenantId, cancellationToken)).Select(row => row.Value));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_custom_fields_address_opens_the_custom_fields_panel_under_its_prefix()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsView);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId));
        using var page = circuit.Page("/tenant/management/settings/custom-fields");
        var rendered = page.Render(builder => Host<Settings>(builder));
        var fields = rendered.WaitForComponent<CustomFieldsPanel>();

        Assert.Equal("fields", fields.Instance.AddressPrefix);
        Assert.Empty(rendered.FindComponents<GeneralSettingsPanel>());
        Assert.Equal("page", rendered.Find("nav[aria-label='Settings'] a[href='/tenant/management/settings/custom-fields']").GetAttribute("aria-current"));
        Assert.Null(rendered.Find("nav[aria-label='Settings'] a[href='/tenant/management/settings']").GetAttribute("aria-current"));
        Assert.Equal("Custom fields", rendered.Find("h2.page-section__title").TextContent.Trim());

        await rendered.InvokeAsync(() => page.Services.GetRequiredService<NavigationManager>().NavigateTo("/tenant/management/settings"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<GeneralSettingsPanel>()), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindComponents<CustomFieldsPanel>());
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_viewer_sees_the_time_zone_read_only_and_someone_without_SettingsView_no_panel()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.SettingsView);
        var stranger = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Stranger", cancellationToken);
        await AddRowAsync(scenario.A, "Mars/Olympus_Mons", cancellationToken);

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(viewer)))
        {
            using var page = circuit.Page("/tenant/management/settings");
            var rendered = page.Render(builder => Host<Settings>(builder));
            var panel = PanelOf(rendered);

            Assert.True(panel.FindComponent<RadzenDropDown<string>>().Instance.Disabled);
            Assert.Contains("You can view the settings but not change them.", panel.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(panel.FindAll("button"), button => ButtonText(button) is "Save" or UsePlatformZone);

            Assert.Equal($"The company's saved time zone, Mars/Olympus_Mons, is not known on this server. Until then, times are shown in the platform's time zone, {Stockholm}.", Notice(panel));
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(stranger)))
        {
            using var page = circuit.Page("/tenant/management/settings");
            var rendered = page.Render(builder => Host<Settings>(builder));
            rendered.WaitForAssertion(() => Assert.Contains("You do not have permission to view settings.", rendered.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            Assert.Empty(rendered.FindComponents<GeneralSettingsPanel>());
            Assert.Empty(rendered.FindAll("nav[aria-label='Settings']"));
        }
    }

    private static IRenderedComponent<GeneralSettingsPanel> PanelOf(IRenderedComponent<IComponent> rendered)
    {
        var panel = rendered.WaitForComponent<GeneralSettingsPanel>();
        rendered.WaitForAssertion(() => Assert.NotNull(panel.Find("[name='settings-time-zone']")), TimeSpan.FromSeconds(10));
        return panel;
    }

    private static string Fact(IRenderedComponent<GeneralSettingsPanel> panel, string label) =>
        panel.FindAll(".general-settings__facts dt").Single(dt => dt.TextContent.Trim() == label).NextElementSibling!.TextContent.Trim();

    private static string Notice(IRenderedComponent<GeneralSettingsPanel> panel) =>
        panel.Find(".general-settings__fallback .rz-alert-message, .general-settings__fallback .rz-alert-content").TextContent.Trim();

    private static string Status(IRenderedComponent<GeneralSettingsPanel> panel) =>
        panel.Find("[role='status'].general-settings__status").TextContent.Trim();

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<GeneralSettingsPanel> panel, string text) =>
        panel.FindAll("button").Single(button => ButtonText(button) == text);

    private static string ButtonText(AngleSharp.Dom.IElement button) =>
        (button.QuerySelector(".rz-button-text")?.TextContent ?? button.TextContent).Trim();

    private async Task AddRowAsync(ScenarioTenant tenant, string value, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        system.TenantSettings.Add(new TenantSetting { TenantId = tenant.TenantId, Key = TenantSettingDefinitions.TimeZone.Key, Value = value, CreatedById = tenant.SystemAccountId });
        await system.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<TenantSetting>> RowsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TenantSettings.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.Key).ToListAsync(cancellationToken);
    }

    private static void Host<TPage>(RenderTreeBuilder builder) where TPage : IComponent
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
        builder.CloseComponent();
        builder.OpenComponent<TPage>(1);
        builder.CloseComponent();
    }

    private async Task<Circuit> CircuitAsync(ClaimsPrincipal cookie)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime);
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly RuntimeExecutionContext runtime;

        public Circuit(ServiceHost host, RuntimeExecutionContext runtime)
        {
            this.host = host;
            this.runtime = runtime;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public PageContext Page(string path)
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton<TimeProvider>(new FakeClock(now));
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<CustomFieldService>());
            page.Services.AddSingleton(this.host.Get<TenantSettingsService>());
            page.Services.GetRequiredService<NavigationManager>().NavigateTo(path);
            return page;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
