using System.Net;
using Microsoft.EntityFrameworkCore;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class SystemPageLayoutTests : IClassFixture<RealDatabase>
{
    private const string Navigation = "aria-label=\"Main navigation\"";
    private const string Loader = "Loading your session";

    private readonly RealDatabase database;

    public SystemPageLayoutTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_console_pages_open()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);

        foreach (var path in new[] { "/superadmin/overview", "/superadmin/tenants", $"/superadmin/tenants/{platform.TenantId}", $"/superadmin/tenants/{platform.TenantId}/roles", "/superadmin/settings" })
        {
            using var response = await browser.Http.GetAsync(path, cancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}");
        }
    }

    [Theory]
    [InlineData("/no-such-page", HttpStatusCode.NotFound, "Not found")]
    [InlineData("/Account/AccessDenied", HttpStatusCode.OK, "Access denied")]
    [InlineData("/Error", HttpStatusCode.OK, "Something went wrong")]
    public async Task A_system_page_shows_a_tenant_users_layout_and_navigation(string path, HttpStatusCode status, string heading)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(scenario.A.OwnerId);

        using var response = await browser.Http.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Contains(heading, body);
        Assert.DoesNotContain(Loader, body);
        Assert.Contains(Navigation, body);
        Assert.Contains("href=\"/me/overview\"", body);
        Assert.Contains("data-app-shell", body);
        Assert.DoesNotContain("app-shell--platform", body);
        Assert.DoesNotContain("href=\"/superadmin/", body);
    }

    [Fact]
    public async Task A_status_page_shows_a_platform_owner_the_console_navigation_and_no_tenant_page()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);

        using var response = await browser.Http.GetAsync("/no-such-page", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Not found", body);
        Assert.Contains(Navigation, body);
        Assert.Contains("href=\"/superadmin/tenants\"", body);
        Assert.DoesNotContain("href=\"/me/", body);
        Assert.DoesNotContain("href=\"/tenant/", body);
    }

    [Theory]
    [InlineData("/no-such-page", HttpStatusCode.NotFound, "Not found")]
    [InlineData("/Account/AccessDenied", HttpStatusCode.OK, "Access denied")]
    public async Task A_system_page_shows_an_anonymous_visitor_the_plain_frame(string path, HttpStatusCode status, string heading)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();

        using var response = await browser.Http.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Contains(heading, body);
        Assert.Contains("anonymous-layout", body);
        Assert.DoesNotContain(Navigation, body);
        Assert.DoesNotContain("account-menu", body);
    }

    [Fact]
    public async Task A_refused_session_gets_no_empty_navigation_and_no_own_account_link()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suspended = await TwoTenantScenario.AddTenantAsync(this.database, "Suspended company", cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(suspended.OwnerId);
        await using (var context = this.database.CreateSystemContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Tenants] SET [State] = {(int)Staffora.Core.Entities.Tenancy.TenantState.Suspended} WHERE [Id] = {suspended.TenantId}", cancellationToken);
        }

        using var response = await browser.Http.GetAsync("/no-such-page", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("anonymous-layout", body);
        Assert.DoesNotContain("account-menu", body);
        Assert.DoesNotContain(Navigation, body);
        Assert.DoesNotContain("href=\"/me/profile\"", body);
    }

    [Fact]
    public async Task A_rate_limited_request_answers_in_the_plain_frame_without_resolving_the_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database.ConnectionString, "Test", new Dictionary<string, string>
        {
            ["RateLimits:Impersonation:PermitLimit"] = "1",
            ["RateLimits:Impersonation:Window"] = "00:01:00",
        });
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);

        (await browser.PostFormAsync("/Account/Impersonation/End")).Dispose();
        using var refused = await browser.PostFormAsync("/Account/Impersonation/End");
        var body = await refused.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("Too many attempts", body);
        Assert.Contains("anonymous-layout", body);
        Assert.DoesNotContain(Navigation, body);
        Assert.DoesNotContain("account-menu", body);
    }
}
