using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Layout;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Test.Web.Layout;

public class NavMenuTests : WebTestContext
{
    private readonly RuntimeExecutionContext runtime = new();

    public NavMenuTests()
    {
        Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
        Services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>>(new UnreachableDbContextFactory("The navigation"));
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<PermissionService>();
        Services.AddScoped<ImpersonationAuthorizer>();
        Services.AddScoped<ImpersonationAuditWriter>();
        Services.AddScoped<UserSessionService>();
        AddAuthorization();
    }

    [Fact]
    public void A_user_without_a_permission_does_not_see_the_link_it_would_open()
    {
        this.runtime.SetUser(Guid.NewGuid());

        var menu = Render<NavMenu>();

        Assert.Equal(["/me/overview"], Links(menu));
        Assert.DoesNotContain("Users", menu.Markup);
        Assert.DoesNotContain("Organization", menu.Markup);
    }

    [Fact]
    public void A_platform_owner_who_is_not_impersonating_sees_the_console_and_no_tenant_page()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);

        var menu = Render<NavMenu>();

        Assert.Equal(AppNavigation.Platform.SelectMany(section => section.Items).Select(item => item.Path), Links(menu));
        Assert.DoesNotContain(Links(menu), path => !path.StartsWith("/superadmin/", StringComparison.Ordinal));
    }

    [Fact]
    public void An_impersonating_platform_owner_sees_the_impersonated_users_links_and_not_the_console()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        this.runtime.StartImpersonation(Guid.NewGuid(), tenantId: null);

        var menu = Render<NavMenu>();

        Assert.Equal(["/me/overview"], Links(menu));
    }

    [Fact]
    public async Task The_menu_follows_the_session_when_it_signs_out()
    {
        this.runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        this.runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        var menu = Render<NavMenu>();
        Assert.NotEmpty(Links(menu));

        await menu.InvokeAsync(Services.GetRequiredService<UserSessionService>().SignOutAsync);

        menu.WaitForAssertion(() => Assert.Empty(Links(menu)));
    }

    [Fact]
    public void The_menu_is_a_labelled_navigation_landmark_of_plain_links_with_decorative_icons()
    {
        this.runtime.SetUser(Guid.NewGuid());

        var menu = Render<NavMenu>();

        Assert.Equal("Main navigation", menu.Find("nav").GetAttribute("aria-label"));
        Assert.All(menu.FindAll("nav a"), link => Assert.Equal("true", link.QuerySelector(".material-symbol")?.GetAttribute("aria-hidden")));
    }

    private static List<string> Links(IRenderedComponent<NavMenu> menu) =>
        [.. menu.FindAll("nav a").Select(link => link.GetAttribute("href")!)];
}
