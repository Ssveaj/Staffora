using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Account;
using Staffora.Blazor.Components.Common.Layout;
using Staffora.Blazor.Layout;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Test.Web.Layout;

public class AppShellTests : WebTestContext
{
    private readonly RuntimeExecutionContext runtime = new();

    public AppShellTests()
    {
        Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
        Services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>>(new UnreachableDbContextFactory("The app shell"));
        Services.AddScoped<PermissionService>();
        Services.AddScoped<ImpersonationAuthorizer>();
        Services.AddScoped<ImpersonationAuditWriter>();
        Services.AddScoped<UserSessionService>();
        AddAuthorization();

        ComponentFactories.AddStub<AccountMenu>();
        ComponentFactories.AddStub<GlobalSearchButton>("<span data-search-stub></span>");

        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
    }

    [Fact]
    public void The_sidebar_holds_the_brand_and_the_navigation_beside_one_banner_with_the_breadcrumb_and_the_account_trigger()
    {
        Navigate("/superadmin/overview");

        var shell = Render<AppShell>(parameters => parameters
            .Add(component => component.Title, "Staffora")
            .Add(component => component.Icon, "groups")
            .Add(component => component.Platform, true)
            .AddChildContent("<p>The page</p>"));

        var sidebar = shell.Find("#app-sidebar");
        var brand = sidebar.QuerySelector("a.app-sidebar__brand")!;
        Assert.Equal("/", brand.GetAttribute("href"));
        Assert.Equal("Staffora", brand.QuerySelector(".app-sidebar__brand-name")?.TextContent);
        Assert.NotNull(sidebar.QuerySelector("nav[aria-label='Main navigation']"));
        Assert.Equal(["Collapse menu", "Expand menu", "Close menu"], sidebar.QuerySelectorAll("button").Select(button => button.GetAttribute("aria-label")));

        var banner = Assert.Single(shell.FindAll("header"));
        Assert.False(sidebar.Contains(banner));
        var menu = banner.QuerySelector("button[data-sidebar-open]")!;
        Assert.Equal("Open menu", menu.GetAttribute("aria-label"));
        Assert.Equal("app-sidebar", menu.GetAttribute("aria-controls"));
        Assert.Equal("false", menu.GetAttribute("aria-expanded"));
        Assert.NotNull(banner.QuerySelector("nav[aria-label='Breadcrumb']"));
        Assert.Single(shell.FindComponents<Stub<AccountMenu>>());
        Assert.Single(shell.FindComponents<Stub<GlobalSearchButton>>());
        Assert.NotNull(banner.QuerySelector(".app-topbar__end > [data-search-stub]"));

        Assert.All(shell.FindAll("button"), button => Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label"))));
        Assert.Equal("The page", Assert.Single(shell.FindAll("main#main-content")).TextContent.Trim());
        Assert.Equal("#main-content", shell.Find("a[data-skip-link]").GetAttribute("href"));
    }

    [Fact]
    public void The_breadcrumb_names_the_section_and_the_page_and_links_the_section_to_its_first_page()
    {
        Navigate("/superadmin/overview");
        var crumbs = Render<AppBreadcrumb>();
        Assert.Equal(["Platform", "Overview"], Steps(crumbs));
        Assert.Empty(crumbs.FindAll("a"));
        Assert.Equal("Overview", crumbs.Find("[aria-current='page']").TextContent);

        Navigate("/superadmin/tenants?state=active");
        crumbs.WaitForAssertion(() => Assert.Equal(["Platform", "Tenants"], Steps(crumbs)));
        Assert.Equal("/superadmin/overview", crumbs.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void A_page_under_a_link_adds_the_link_and_ends_in_the_title_of_its_header()
    {
        Navigate("/superadmin/tenants/0b7a6f3e-55f5-4a43-9a7c-3f8c2a3b9d10");

        var page = Render(builder =>
        {
            builder.OpenComponent<AppBreadcrumb>(0);
            builder.CloseComponent();
            builder.OpenComponent<PageHeader>(1);
            builder.AddComponentParameter(2, nameof(PageHeader.Title), "Company A");
            builder.CloseComponent();
        });

        var crumbs = page.Find("nav[aria-label='Breadcrumb']");
        Assert.Equal(["Platform", "Tenants", "Company A"], crumbs.QuerySelectorAll("li").Select(step => step.TextContent.Trim()));
        Assert.Equal(["/superadmin/overview", "/superadmin/tenants"], crumbs.QuerySelectorAll("a").Select(link => link.GetAttribute("href")));
        Assert.Equal("Company A", crumbs.QuerySelector("[aria-current='page']")?.TextContent);
    }

    [Fact]
    public void The_last_step_keeps_the_page_headers_title_as_the_address_moves_between_kinds_of_page()
    {
        Navigate("/superadmin/tenants/0b7a6f3e-55f5-4a43-9a7c-3f8c2a3b9d10");
        var page = Render(builder =>
        {
            builder.OpenComponent<AppBreadcrumb>(0);
            builder.CloseComponent();
            builder.OpenComponent<PageHeader>(1);
            builder.AddComponentParameter(2, nameof(PageHeader.Title), "Tenants");
            builder.CloseComponent();
        });

        foreach (var path in new[] { "/superadmin/tenants", "/Account/AccessDenied", "/superadmin/tenants/0b7a6f3e-55f5-4a43-9a7c-3f8c2a3b9d10", "/superadmin/overview" })
        {
            Navigate(path);
            page.WaitForAssertion(() => Assert.Equal("Tenants", page.Find("[aria-current='page'] h1").TextContent));
            Assert.Single(page.FindAll("h1"));
        }
    }

    [Fact]
    public void A_page_outside_the_navigation_shows_only_its_title()
    {
        Navigate("/Account/AccessDenied");

        var page = Render(builder =>
        {
            builder.OpenComponent<AppBreadcrumb>(0);
            builder.CloseComponent();
            builder.OpenComponent<PageHeader>(1);
            builder.AddComponentParameter(2, nameof(PageHeader.Title), "Access denied");
            builder.CloseComponent();
        });

        Assert.Equal(["Access denied"], page.Find("nav[aria-label='Breadcrumb']").QuerySelectorAll("li").Select(step => step.TextContent.Trim()));
    }

    [Fact]
    public void The_sidebar_marks_the_link_of_the_page_or_of_the_page_above_it_and_names_each_link()
    {
        Navigate("/superadmin/tenants");
        var menu = Render<NavMenu>();
        var tenants = menu.Find("a[href='/superadmin/tenants']");
        Assert.Contains("active", tenants.ClassList);
        Assert.Equal("page", tenants.GetAttribute("aria-current"));
        Assert.Single(menu.FindAll("a.active"));

        Navigate("/superadmin/tenants/0b7a6f3e-55f5-4a43-9a7c-3f8c2a3b9d10");
        menu.WaitForAssertion(() => Assert.Null(menu.Find("a[href='/superadmin/tenants']").GetAttribute("aria-current")));
        Assert.Contains("active", menu.Find("a[href='/superadmin/tenants']").ClassList);

        Assert.All(menu.FindAll("a.app-nav__link"), link =>
        {
            var label = link.QuerySelector(".app-nav__label")!.TextContent;
            Assert.Equal(label, link.GetAttribute("aria-label"));
            Assert.Equal(label, link.GetAttribute("title"));
        });
        Assert.All(menu.FindAll("ul.app-nav__list"), list => Assert.NotNull(menu.Find($"#{list.GetAttribute("aria-labelledby")}")));
        Assert.Equal(AppNavigation.Platform.Select(section => section.Title), menu.FindAll(".app-nav__heading").Select(heading => heading.TextContent));
    }

    [Fact]
    public void The_fold_is_read_from_its_cookie_and_nothing_else_folds_the_sidebar()
    {
        Assert.Equal("collapsed", SidebarPreference.Attribute(WithCookie("collapsed")));
        Assert.Null(SidebarPreference.Attribute(WithCookie("expanded")));
        Assert.Null(SidebarPreference.Attribute(new DefaultHttpContext()));
        Assert.Null(SidebarPreference.Attribute(null));
    }

    private void Navigate(string path)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(path);
        Assert.EndsWith(path, navigation.Uri, StringComparison.Ordinal);
    }

    private static List<string> Steps(IRenderedComponent<AppBreadcrumb> crumbs) =>
        [.. crumbs.FindAll("li").Select(step => step.TextContent.Trim())];

    private static DefaultHttpContext WithCookie(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SidebarPreference.CookieName}={value}";
        return context;
    }
}
