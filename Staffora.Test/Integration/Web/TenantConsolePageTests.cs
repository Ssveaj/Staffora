using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Staffora.Blazor.Pages.SuperAdmin;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Core.Lists;
using Microsoft.EntityFrameworkCore;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Integration;
using Staffora.Services.Platform;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class TenantConsolePageTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly string[] writes = ["New", "Add", "Create", "Save", "Delete", "Remove", "Edit", "Deactivate", "Reactivate", "Archive", "New password"];

    private readonly RealDatabase database;

    public TenantConsolePageTests(RealDatabase database)
    {
        this.database = database;
    }

    [Theory]
    [InlineData(TenantState.Setup)]
    [InlineData(TenantState.Active)]
    [InlineData(TenantState.Suspended)]
    [InlineData(TenantState.PendingDeletion)]
    [InlineData(TenantState.Archived)]
    public async Task The_console_offers_the_platform_owner_no_write_into_a_tenant_in_any_state(TenantState state)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName($"Company {state}"), cancellationToken, state);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());

        foreach (var (tab, shown) in new (string?, string)[] { (null, "System owners"), ("users", tenant.Name), ("roles", "Roles"), ("custom-fields", "Custom fields"), ("dead-letters", "No dead letters") })
        {
            var page = await circuit.RenderTenantConsoleAsync(tenant.TenantId, tab);
            page.WaitForAssertion(() => Assert.Contains(shown, page.Find("main, .page-scaffold").TextContent, StringComparison.Ordinal), Wait);
            page.WaitForAssertion(() => Assert.Empty(page.FindAll("[role=progressbar], .list-view__loading")), Wait);
            var offered = page.FindAll("button").Select(ButtonText).Where(text => text.Length > 0).ToList();
            Assert.Contains("Tenant details", offered);
            Assert.DoesNotContain(offered, text => writes.Any(write => text.StartsWith(write, StringComparison.Ordinal)));
            Assert.Empty(page.FindAll("button[aria-label^='Remove']"));
        }
    }

    [Fact]
    public async Task Each_tab_shows_its_tenants_rows_at_its_own_address_and_nothing_of_another_tenant()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.SettingsView);
        var fieldOfA = await AddFieldAsync(scenario.A, "Badge of A", cancellationToken);
        var fieldOfB = await AddFieldAsync(scenario.B, "Badge of B", cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());

        var overview = await circuit.RenderTenantConsoleAsync(scenario.A.TenantId, tab: null);
        overview.WaitForAssertion(() => Assert.Equal(scenario.A.Name, overview.Find("h1").TextContent.Trim()), Wait);
        Assert.Equal("Overview", CurrentTab(overview));
        Assert.DoesNotContain("State: Active", overview.Markup, StringComparison.Ordinal);
        Assert.Equal("Active", overview.Find(".console-hero [data-console-state]").TextContent.Trim());
        var figures = overview.Find("[aria-label='The tenant in figures']");
        Assert.Equal(["active-users", "inactive-users", "units", "roles", "dead-letters"], figures.QuerySelectorAll("[data-figure]").Select(figure => figure.GetAttribute("data-figure")).ToList());
        Assert.Equal($"/superadmin/tenants/{scenario.A.TenantId}/users", figures.QuerySelector("[data-figure='active-users'] a")!.GetAttribute("href"));
        Assert.Single(overview.FindAll("h1, h2"), heading => heading.TextContent.Trim() == scenario.A.Name && heading.TagName == "H2");
        Assert.All(overview.FindAll("[role=treeitem]"), item => Assert.Equal("false", item.GetAttribute("aria-selected")));
        Assert.Empty(overview.FindAll(".console-unit"));
        overview.Find("[role=treeitem]").Click();
        overview.WaitForAssertion(() => Assert.Equal("true", overview.Find("[role=treeitem]").GetAttribute("aria-selected")), Wait);
        Assert.Equal(scenario.A.Name, overview.Find(".console-unit .console-unit__name").TextContent.Trim());
        Assert.DoesNotContain(scenario.B.Name, overview.Markup, StringComparison.Ordinal);

        var users = await circuit.RenderTenantConsoleAsync(scenario.A.TenantId, "users");
        users.WaitForAssertion(() => Assert.NotEmpty(users.FindAll($"tr.list-row[data-row='{scenario.A.OwnerId}']")), Wait);
        Assert.Equal("Users", CurrentTab(users));
        Assert.Single(users.FindAll("tr.list-row"));
        Assert.Empty(users.FindAll($"tr.list-row[data-row='{scenario.A.SystemAccountId}']"));
        Assert.Empty(users.FindAll($"tr.list-row[data-row='{scenario.B.OwnerId}']"));

        var ownerRow = users.Find($"tr.list-row[data-row='{scenario.A.OwnerId}']");
        Assert.Equal(["Name", "Roles", "Last signed in", "Member since"], ownerRow.QuerySelectorAll("td[data-label]").Select(cell => cell.GetAttribute("data-label")).Take(4).ToList());
        Assert.EndsWith("Never", ownerRow.QuerySelector("td[data-label='Last signed in']")!.TextContent.Trim(), StringComparison.Ordinal);
        Assert.Matches(@"\d{4}-\d{2}-\d{2}$", ownerRow.QuerySelector("td[data-label='Member since']")!.TextContent.Trim());

        ownerRow.Click();
        users.WaitForAssertion(() => Assert.NotEmpty(users.FindAll(".state-entity-dialog [name='user-first-name']")), Wait);
        users.WaitForAssertion(() => Assert.NotEmpty(users.FindAll(".state-entity-dialog-footer__bar[aria-busy='false']")), Wait);
        List<string?> Steps() => [.. users.FindAll(".state-entity-dialog-footer button[data-transition]").Select(button => button.GetAttribute("data-transition"))];
        var steps = Steps();
        Assert.NotNull(users.EditButton());
        Assert.Single(steps, step => step == ApplicationUserWorkflowAttribute.NewPasswordStep);
        Assert.DoesNotContain(steps, step => step is ApplicationUserWorkflowAttribute.DeactivateStep or ApplicationUserWorkflowAttribute.PortalNewPasswordStep);
        users.Edit();
        Assert.Single(Steps(), step => step == ApplicationUserWorkflowAttribute.SaveStep);
        Assert.True(users.Find(".state-entity-dialog [name='user-phone']").HasAttribute("readonly"));

        var roles = await circuit.RenderTenantConsoleAsync(scenario.A.TenantId, "roles");
        roles.WaitForAssertion(() => Assert.Single(roles.FindAll("tr.list-row")), Wait);
        Assert.Equal("Roles", CurrentTab(roles));
        Assert.Contains("1 person", roles.Find("tr.list-row").TextContent, StringComparison.Ordinal);

        var settings = await circuit.RenderTenantConsoleAsync(scenario.A.TenantId, "custom-fields");
        settings.WaitForAssertion(() => Assert.NotEmpty(settings.FindAll($"tr.list-row[data-row='{fieldOfA}']")), Wait);
        Assert.Equal("Custom fields", CurrentTab(settings));
        Assert.Empty(settings.FindAll(".console-settings, a[href='/superadmin/settings']"));
        Assert.Empty(settings.FindAll($"tr.list-row[data-row='{fieldOfB}']"));
        Assert.NotNull(settings.Find($"tr.list-row[data-row='{fieldOfA}'] .list-row__open"));
        Assert.Contains("Active", settings.Find($"tr.list-row[data-row='{fieldOfA}']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(settings.FindAll("button"), button => ButtonText(button) == "New field");

        var unknown = await circuit.RenderTenantConsoleAsync(scenario.A.TenantId, "no-such-tab");
        unknown.WaitForAssertion(() => Assert.Equal("Overview", CurrentTab(unknown)), Wait);
    }

    [Fact]
    public async Task A_tenant_the_platform_does_not_hold_shows_not_found_and_no_tab()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());

        var page = await circuit.RenderTenantConsoleAsync(Guid.NewGuid(), "users");
        page.WaitForAssertion(() => Assert.Contains(PlatformConsoleService.TenantNotFound, page.Find(".empty-state[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(page.FindAll("nav.console-tabs"));
        Assert.DoesNotContain(page.FindAll("button"), button => ButtonText(button) == "Tenant details");
    }

    [Fact]
    public async Task The_tenant_list_links_every_row_to_its_console_offers_no_filter_and_leaves_the_platform_out_and_the_overview_counts_each_state()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var suspended = await TwoTenantScenario.AddTenantAsync(this.database, "Linked Suspended", cancellationToken, TenantState.Suspended);
        var active = await TwoTenantScenario.AddTenantAsync(this.database, "Linked Active", cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());

        var overview = await circuit.RenderAsync<SuperAdminOverview>();
        overview.WaitForAssertion(() => Assert.Equal(Enum.GetValues<TenantState>().Length, overview.FindAll(".state-count").Count), Wait);
        Assert.Equal(
            Enum.GetValues<TenantState>().Select(PlatformTenantList.StateValue),
            overview.FindAll(".state-count").Select(count => count.GetAttribute("data-state")));
        Assert.Empty(overview.FindAll("a.state-count"));
        Assert.Single(overview.FindAll("button"), button => ButtonText(button) == "Impersonate User");
        Assert.Contains("/superadmin/errorlog", overview.FindAll("a").Select(link => link.GetAttribute("href")));

        circuit.NavigateTo("/superadmin/tenants?state=suspended&q=Linked");
        var list = await circuit.RenderAsync<Tenants>();
        list.WaitForAssertion(() => Assert.NotEmpty(list.FindAll($"tr.list-row[data-row='{suspended.TenantId}']")), Wait);
        Assert.NotEmpty(list.FindAll($"tr.list-row[data-row='{active.TenantId}']"));
        Assert.Empty(list.FindAll(".list-chip, .list-quick"));
        Assert.Empty(list.FindAll("thead th button"));
        var link = list.Find($"tr.list-row[data-row='{suspended.TenantId}'] a.list-row__open");
        Assert.Equal((suspended.Name, $"/superadmin/tenants/{suspended.TenantId}"), (link.TextContent.Trim(), link.GetAttribute("href")));

        list.Find($"tr.list-row[data-row='{suspended.TenantId}']").Click();
        Assert.EndsWith($"/superadmin/tenants/{suspended.TenantId}", circuit.CurrentUri, StringComparison.Ordinal);

        Assert.Contains("None", list.Find($"tr.list-row[data-row='{suspended.TenantId}'] td[data-label='Managed customers']").TextContent, StringComparison.Ordinal);

        Assert.Empty(list.FindAll("button.list-expand"));

        circuit.NavigateTo($"/superadmin/tenants?q={Uri.EscapeDataString(platform.Name)}");
        var platformList = await circuit.RenderAsync<Tenants>();
        platformList.WaitForAssertion(() => Assert.True(platformList.FindAll("tr.list-row").Count > 0 || platformList.Markup.Contains("No tenants match", StringComparison.Ordinal)), Wait);
        Assert.Empty(platformList.FindAll($"tr.list-row[data-row='{platform.TenantId}']"));
    }

    [Fact]
    public async Task The_list_shows_the_staffing_company_naming_its_customer_tenant_and_the_consoles_show_the_link_and_the_dead_letters()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var family = $"Family {Guid.NewGuid().ToString("N")[..6]}";
        var company = await TwoTenantScenario.AddTenantAsync(this.database, $"{family} Beta Staffing", cancellationToken);
        var customer = await TwoTenantScenario.AddTenantAsync(this.database, $"{family} Alpha Portal", cancellationToken);
        var stranger = await TwoTenantScenario.AddTenantAsync(this.database, $"{family} Gamma", cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET ParentTenantId = {company.TenantId} WHERE Id = {customer.TenantId}", cancellationToken);
        }

        var clock = new ManualClock(new DateTimeOffset(2027, 2, 1, 8, 0, 0, TimeSpan.Zero));
        Guid letter;
        await using (var context = this.database.CreateContext(company.Runtime(), clock))
        {
            letter = IntegrationOutbox.Enqueue(context, "test.dead-letter", stranger.TenantId, Guid.NewGuid(), "{}").Id;
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var dispatcher = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        }))
        {
            Assert.Equal(1, (await dispatcher.Get<IntegrationOutboxDispatcher>().RunOnceAsync(cancellationToken)).DeadLettered);
        }

        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());

        circuit.NavigateTo($"/superadmin/tenants?q={Uri.EscapeDataString(family)}");
        var list = await circuit.RenderAsync<Tenants>();
        list.WaitForAssertion(() => Assert.Equal(2, list.FindAll("tr.list-row").Count), Wait);
        Assert.Equal([$"{company.TenantId}", $"{stranger.TenantId}"], list.FindAll("tr.list-row").Select(row => row.GetAttribute("data-row")));
        Assert.Empty(list.FindAll($"tr.list-row[data-row='{customer.TenantId}']"));
        Assert.Empty(list.FindAll("td[data-label='Managed by']"));
        var staffing = list.Find($"tr.list-row[data-row='{company.TenantId}']");
        Assert.Contains(customer.Name, staffing.QuerySelector("td[data-label='Managed customers']")!.TextContent, StringComparison.Ordinal);

        var overview = await circuit.RenderTenantConsoleAsync(company.TenantId, tab: null);
        overview.WaitForAssertion(() => Assert.NotEmpty(overview.FindAll($"[data-managed-customer='{customer.TenantId}']")), Wait);
        Assert.Equal($"/superadmin/tenants/{customer.TenantId}", overview.Find($"[data-managed-customer='{customer.TenantId}'] a").GetAttribute("href"));
        Assert.Equal("1", overview.Find("[aria-label='The tenant in figures'] [data-figure='dead-letters'] .console-hero__value").TextContent.Trim());
        Assert.Equal("attention", overview.Find("[data-figure='dead-letters']").GetAttribute("data-tone"));

        var customerOverview = await circuit.RenderTenantConsoleAsync(customer.TenantId, tab: null);
        customerOverview.WaitForAssertion(() => Assert.NotEmpty(customerOverview.FindAll($"[data-managed-by='{company.TenantId}']")), Wait);
        Assert.Equal($"/superadmin/tenants/{company.TenantId}", customerOverview.Find($"[data-managed-by='{company.TenantId}'] a").GetAttribute("href"));
        Assert.Empty(customerOverview.FindAll("[data-managed-customer]"));
        Assert.Contains("A customer portal has none", customerOverview.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("No active system owner", customerOverview.Markup, StringComparison.Ordinal);

        var letters = await circuit.RenderTenantConsoleAsync(company.TenantId, "dead-letters");
        letters.WaitForAssertion(() => Assert.NotEmpty(letters.FindAll($"tr.list-row[data-row='{letter}']")), Wait);
        Assert.Equal("Dead letters", CurrentTab(letters));
        var row = letters.Find($"tr.list-row[data-row='{letter}']");
        Assert.Contains("test.dead-letter", row.TextContent, StringComparison.Ordinal);
        Assert.Contains($"Sent to {stranger.Name}", row.TextContent, StringComparison.Ordinal);
        Assert.Contains(IntegrationOutboxDispatcher.NotLinkedReason, row.TextContent, StringComparison.Ordinal);

        var received = await circuit.RenderTenantConsoleAsync(stranger.TenantId, "dead-letters");
        received.WaitForAssertion(() => Assert.Contains($"From {company.Name}", received.Find($"tr.list-row[data-row='{letter}']").TextContent, StringComparison.Ordinal), Wait);
        var none = await circuit.RenderTenantConsoleAsync(customer.TenantId, "dead-letters");
        none.WaitForAssertion(() => Assert.Contains("No dead letters", none.Find("main, .page-scaffold").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(none.FindAll("tr.list-row"));
    }

    private async Task<Guid> AddFieldAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var field = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = name, ValueType = AttributeValueType.Text, TenantId = tenant.TenantId, CreatedById = tenant.OwnerId };
        system.Add(field);
        await system.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private static string ButtonText(IElement button) =>
        (button.QuerySelector(".rz-button-text")?.TextContent ?? button.TextContent).Trim();

    private static string CurrentTab(IRenderedComponent<IComponent> page) =>
        page.Find(".console-tabs a[aria-current=page] span:not(.material-symbol)").TextContent.Trim();

    private sealed class ConsoleCircuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly PageContext page = new();

        public ConsoleCircuit(RealDatabase database, IRuntimeExecutionContext runtime)
        {
            this.host = ServiceHost.Create(database, runtime, services =>
            {
                services.AddSingleton<AuthenticationStateProvider>(new Anonymous());
                services.AddScoped<UserSessionService>();
            });
            this.page.Services.AddLogging();
            this.page.Services.AddSingleton(runtime);
            this.page.Services.AddSingleton(TimeProvider.System);
            this.page.Services.AddSingleton(this.host.Get<PermissionService>());
            this.page.Services.AddSingleton(this.host.Get<WorkflowService>());
            this.page.Services.AddSingleton(this.host.Get<TenantService>());
            this.page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            this.page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            this.page.Services.AddSingleton(this.host.Get<TenantOnboardingService>());
            this.page.Services.AddSingleton(this.host.Get<OrganizationService>());
            this.page.Services.AddSingleton(this.host.Get<UserService>());
            this.page.Services.AddSingleton(this.host.Get<PlatformConsoleService>());
            this.page.Services.AddSingleton(this.host.Get<CustomFieldService>());
            this.page.Services.AddSingleton(this.host.Get<TenantSettingsService>());
            this.page.Services.AddSingleton(this.host.Get<UserSessionService>());

            this.page.Services.AddSingleton<AuthenticationStateProvider>(new Anonymous());
        }

        public string CurrentUri => this.page.Services.GetRequiredService<NavigationManager>().Uri;

        public void NavigateTo(string uri) => this.page.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);

        public async Task<IRenderedComponent<IComponent>> RenderAsync<TPage>() where TPage : IComponent
        {
            await this.page.DisposeComponentsAsync();
            return this.page.Render(builder => Host<TPage>(builder, null));
        }

        public async Task<IRenderedComponent<IComponent>> RenderTenantConsoleAsync(Guid tenantId, string? tab)
        {
            await this.page.DisposeComponentsAsync();
            return this.page.Render(builder => Host<TenantConsole>(builder, inner =>
            {
                inner.AddComponentParameter(2, nameof(TenantConsole.TenantId), tenantId);
                inner.AddComponentParameter(3, nameof(TenantConsole.Tab), tab);
            }));
        }

        private static void Host<TPage>(RenderTreeBuilder builder, Action<RenderTreeBuilder>? parameters) where TPage : IComponent
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
            builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
            builder.CloseComponent();
            builder.OpenComponent<TPage>(1);
            parameters?.Invoke(builder);
            builder.CloseComponent();
        }

        public async ValueTask DisposeAsync()
        {
            await this.page.DisposeAsync();
            await this.host.DisposeAsync();
        }
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    private sealed class PageContext : WebTestContext;
}
