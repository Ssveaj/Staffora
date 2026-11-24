using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Layout;
using Staffora.Blazor.Pages;
using Staffora.Blazor.Pages.Portal;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Portal;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class PortalPagesTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public PortalPagesTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_portal_user_reaching_a_page_that_is_not_part_of_the_portal_sees_not_part_of_your_portal_and_the_page_never_renders()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Guarded Staffing", Ct);

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.AdministratorId)))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Guarded(builder, typeof(Jobs)));
            rendered.WaitForAssertion(() => Assert.Contains(CustomerPortalPages.NotPartTitle, rendered.Find("h1").TextContent, StringComparison.Ordinal), Wait);
            Assert.Contains("not part of your portal", rendered.Markup, StringComparison.Ordinal);
            Assert.Empty(rendered.FindAll("[data-guarded-page]"));
            Assert.Equal(AppHome.PortalHome, rendered.Find("a.system-page__link").GetAttribute("href"));

            var portal = page.Render(builder => Guarded(builder, typeof(PortalHome)));
            portal.WaitForAssertion(() => Assert.Single(portal.FindAll("[data-guarded-page]")), Wait);
            Assert.DoesNotContain(CustomerPortalPages.NotPartTitle, portal.Markup, StringComparison.Ordinal);
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.Supplier.OwnerId)))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Guarded(builder, typeof(Jobs)));
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("[data-guarded-page]")), Wait);
            Assert.DoesNotContain(CustomerPortalPages.NotPartTitle, rendered.Markup, StringComparison.Ordinal);
        }

        await using (var circuit = await CircuitAsync(await ImpersonatingAsync(scenario.Supplier.Platform.OwnerId, scenario.AdministratorId)))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Guarded(builder, typeof(Jobs)));
            rendered.WaitForAssertion(() => Assert.Contains(CustomerPortalPages.NotPartTitle, rendered.Find("h1").TextContent, StringComparison.Ordinal), Wait);
            Assert.Empty(rendered.FindAll("[data-guarded-page]"));
        }
    }

    [Fact]
    public async Task The_start_page_sends_a_portals_session_to_the_portals_home_and_the_home_names_the_supplier()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Home Staffing", Ct);
        Assert.Equal(AppHome.PortalHome, await LandAsync(SessionCircuit.Cookie(scenario.AdministratorId)));
        Assert.Equal(AppHome.PortalHome, await LandAsync(await ImpersonatingAsync(scenario.Supplier.Platform.OwnerId, scenario.AdministratorId)));
        Assert.Equal(AppHome.SetupChecklist, await LandAsync(SessionCircuit.Cookie(scenario.Supplier.OwnerId)));

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.AdministratorId)))
        {
            using var page = circuit.Page();
            var rendered = page.Render<PortalHome>();
            rendered.WaitForAssertion(() => Assert.Equal("ready", rendered.Find("[data-portal-home]").GetAttribute("data-portal-home")), Wait);
            Assert.Contains(scenario.Supplier.Tenant.Name, rendered.Find("[data-portal-supplier]").TextContent, StringComparison.Ordinal);
            Assert.Equal(3, rendered.FindAll(".portal-home__ability").Count);
            Assert.Contains("No orders yet", rendered.Markup, StringComparison.Ordinal);
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.Supplier.OwnerId)))
        {
            using var page = circuit.Page();
            var rendered = page.Render<PortalHome>();
            rendered.WaitForAssertion(() => Assert.Contains("Access denied", rendered.Markup, StringComparison.Ordinal), Wait);
            Assert.Empty(rendered.FindAll("[data-portal-home]"));
        }
    }

    [Fact]
    public async Task The_navigation_of_a_portals_session_holds_the_portals_links_only()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Navigation Staffing", Ct);

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.AdministratorId)))
        {
            using var page = circuit.Page();
            var rendered = page.Render<NavMenu>();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll("a.app-nav__link")), Wait);
            Assert.Equal([AppHome.PortalHome, "/portal/orders", "/portal/orders/new"], rendered.FindAll("a.app-nav__link").Select(link => link.GetAttribute("href")));
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.Supplier.OwnerId)))
        {
            using var page = circuit.Page();
            var rendered = page.Render<NavMenu>();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll("a.app-nav__link")), Wait);
            var links = rendered.FindAll("a.app-nav__link").Select(link => link.GetAttribute("href")).ToList();
            Assert.Contains("/staffing/customers", links);
            Assert.DoesNotContain(AppHome.PortalHome, links);
        }
    }

    [Fact]
    public async Task My_requests_a_requests_page_and_the_home_show_the_portals_requests_and_a_staffing_company_user_given_their_permissions_sees_none()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Orders Staffing", Ct);
        Guid requestId;
        await using (var writer = ServiceHost.Create(this.database, scenario.AdministratorRuntime()))
        {
            var service = writer.Get<OrderService>();
            var fresh = (await service.NewOrderAsync(Ct)).Value!;
            (fresh.Name, fresh.PeriodStart, fresh.PeriodEnd) = ("Algebra for year 9", new DateOnly(2027, 2, 1), new DateOnly(2027, 5, 31));
            fresh.Schedule = Staffora.Core.Entities.Jobs.JobSchedule.Recurring;
            var created = await service.CreateOrderAsync(fresh, Staffora.Core.Workflow.WorkflowAttributes.OrderWorkflowAttribute.SaveDraftStep, Ct);
            Assert.True(created.IsSuccess, created.Error);
            requestId = created.Value;
        }

        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.AdministratorId)))
        {
            using var page = circuit.Page();
            var list = page.Render(builder =>
            {
                builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(0);
                builder.CloseComponent();
                builder.OpenComponent<MyRequests>(1);
                builder.CloseComponent();
            });
            list.WaitForAssertion(() => Assert.Contains("Algebra for year 9", list.Markup, StringComparison.Ordinal), Wait);
            Assert.Equal($"/portal/orders/{requestId}", list.Find($"a[href='/portal/orders/{requestId}']").GetAttribute("href"));
            Assert.Contains("New order", list.Markup, StringComparison.Ordinal);

            var details = page.Render<RequestDetails>(parameters => parameters.Add(shown => shown.RequestId, requestId));
            details.WaitForAssertion(() => Assert.Equal("ready", details.Find("[data-order-details]").GetAttribute("data-order-details")), Wait);
            Assert.Equal("Algebra for year 9", details.Find("[data-order-name]").TextContent.Trim());
            Assert.Equal("Registration", details.Find("[data-order-state]").GetAttribute("data-order-state"));
            Assert.Equal("Not sent yet", details.Find("[data-order-sent]").TextContent.Trim());

            var home = page.Render<PortalHome>();
            home.WaitForAssertion(() => Assert.Single(home.FindAll("[data-portal-orders] li")), Wait);
            Assert.Equal($"/portal/orders/{requestId}", home.Find("[data-portal-orders] a").GetAttribute("href"));
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.Supplier.Tenant, scenario.Supplier.OwnerId, Ct, [.. Staffora.Core.Entities.Tenancy.RolePermissions.Portal]);
        await using (var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.Supplier.OwnerId)))
        {
            using var page = circuit.Page();
            var list = page.Render<MyRequests>();
            list.WaitForAssertion(() => Assert.Contains("Access denied", list.Markup, StringComparison.Ordinal), Wait);
            Assert.DoesNotContain("Algebra for year 9", list.Markup, StringComparison.Ordinal);

            var details = page.Render<RequestDetails>(parameters => parameters.Add(shown => shown.RequestId, requestId));
            details.WaitForAssertion(() => Assert.Contains("Access denied", details.Markup, StringComparison.Ordinal), Wait);
            Assert.Empty(details.FindAll("[data-order-details]"));

            var write = page.Render<NewRequest>();
            write.WaitForAssertion(() => Assert.Contains("Access denied", write.Markup, StringComparison.Ordinal), Wait);
            Assert.Empty(write.FindAll("[data-new-order]"));
        }
    }

    [Fact]
    public async Task A_portals_session_moving_on_to_a_page_that_is_not_part_of_the_portal_never_renders_it_while_its_edition_is_read()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Moving Staffing", Ct);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.AdministratorId));
        using var page = circuit.Page();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<Type?>();

        var rendered = page.Render<RoutedGuard>(parameters => parameters.Add(host => host.Page, typeof(PortalHome)).Add(host => host.Seen, seen));
        rendered.WaitForAssertion(() => Assert.Contains(typeof(PortalHome), seen), Wait);

        await page.Services.GetRequiredService<PermissionService>().InvalidateCacheAsync(Ct);
        rendered.Render(parameters => parameters.Add(host => host.Page, typeof(Jobs)));
        rendered.WaitForAssertion(() => Assert.Contains(CustomerPortalPages.NotPartTitle, rendered.Find("h1").TextContent, StringComparison.Ordinal), Wait);
        Assert.DoesNotContain(typeof(Jobs), seen);
    }

    private sealed class RoutedGuard : ComponentBase
    {
        [Parameter] public Type Page { get; set; } = typeof(PortalHome);

        [Parameter] public System.Collections.Concurrent.ConcurrentQueue<Type?> Seen { get; set; } = new();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<RouteData>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<RouteData>.Value), new RouteData(Page, new Dictionary<string, object?>()));
            builder.AddComponentParameter(2, nameof(CascadingValue<RouteData>.ChildContent), (RenderFragment)(guard =>
            {
                guard.OpenComponent<PortalPageGuard>(0);
                guard.AddComponentParameter(1, nameof(PortalPageGuard.ChildContent), (RenderFragment)(content =>
                {
                    content.OpenComponent<PageProbe>(0);
                    content.SetKey(Page);
                    content.AddComponentParameter(1, nameof(PageProbe.For), Page);
                    content.AddComponentParameter(2, nameof(PageProbe.Seen), Seen);
                    content.CloseComponent();
                }));
                guard.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class PageProbe : ComponentBase
    {
        [Parameter] public Type? For { get; set; }

        [Parameter] public System.Collections.Concurrent.ConcurrentQueue<Type?> Seen { get; set; } = new();

        protected override void OnParametersSet() => Seen.Enqueue(For);

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, "<p data-guarded-page>The page</p>");
    }

    private static void Guarded(RenderTreeBuilder builder, Type page)
    {
        builder.OpenComponent<CascadingValue<RouteData>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<RouteData>.Value), new RouteData(page, new Dictionary<string, object?>()));
        builder.AddComponentParameter(2, nameof(CascadingValue<RouteData>.ChildContent), (RenderFragment)(guard =>
        {
            guard.OpenComponent<PortalPageGuard>(0);
            guard.AddComponentParameter(1, nameof(PortalPageGuard.ChildContent), (RenderFragment)(content => content.AddMarkupContent(0, "<p data-guarded-page>The page</p>")));
            guard.CloseComponent();
        }));
        builder.CloseComponent();
    }

    private async Task<string> LandAsync(ClaimsPrincipal cookie)
    {
        await using var circuit = await CircuitAsync(cookie);
        using var page = circuit.Page();
        var navigation = page.Services.GetRequiredService<NavigationManager>();
        var start = navigation.Uri;
        var rendered = page.Render<Home>();
        rendered.WaitForAssertion(() => Assert.NotEqual(start, navigation.Uri), Wait);
        return "/" + navigation.ToBaseRelativePath(navigation.Uri);
    }

    private async Task<ClaimsPrincipal> ImpersonatingAsync(Guid platformOwner, Guid target)
    {
        await using var endpoint = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var started = await endpoint.Get<ImpersonationService>().StartAsync(SessionCircuit.Cookie(platformOwner, AppRoles.Admin), target, Ct);
        Assert.True(started.IsSuccess, started.Error);
        return started.Value!;
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

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Home.TenantHomeService>());
            page.Services.AddSingleton(this.host.Get<OrderService>());
            page.Services.AddSingleton(this.host.Get<TenantClock>());
            var authorization = page.AddAuthorization();
            authorization.SetAuthorized(this.runtime.EffectiveUserId?.ToString() ?? "user");
            return page;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
