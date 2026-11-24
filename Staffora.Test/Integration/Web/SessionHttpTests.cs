using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Services.Authorization;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class SessionHttpTests : IClassFixture<RealDatabase>
{
    private const string TargetRole = "Coordinator role of A";

    private readonly RealDatabase database;

    public SessionHttpTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_page_is_served_without_prerendering_so_its_html_holds_only_the_loader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(scenario.A.OwnerId);

        using var response = await browser.Http.GetAsync("/tenant/management/users", cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"boot-loader\"", html);
        Assert.DoesNotContain("data-session=\"ready\"", html);
        Assert.DoesNotContain("data-app-shell", html);
    }

    [Fact]
    public async Task An_impersonating_owner_is_refused_the_console_and_told_why_and_the_impersonation_goes_on()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        (await StartAsync(browser, scenario.A.OwnerId)).Dispose();
        var row = await OpenRowAsync(platform.OwnerId, cancellationToken);

        await AssertRefusedAsync(browser, "/superadmin/overview");
        var claims = await browser.ClaimsAsync();
        Assert.Equal([TargetRole], Roles(claims));
        Assert.Equal(row.ToString(), Value(claims, ImpersonationClaims.SessionId));
        Assert.Equal(row, await OpenRowAsync(platform.OwnerId, cancellationToken));

        using var denied = await browser.Http.GetAsync("/Account/AccessDenied", cancellationToken);
        var html = await denied.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("You are impersonating a user", html);
        Assert.Contains("action=\"Account/Impersonation/End\"", html);
    }

    [Fact]
    public async Task A_cookie_refreshed_from_the_user_row_keeps_the_impersonation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        (await StartAsync(browser, scenario.A.OwnerId)).Dispose();
        var before = await browser.ClaimsAsync();

        using (var refreshed = await browser.Http.PostAsync("/test/refresh-sign-in", content: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        }

        var after = await browser.ClaimsAsync();
        Assert.Equal([TargetRole], Roles(after));
        Assert.Equal(Value(before, ImpersonationClaims.TargetUserId), Value(after, ImpersonationClaims.TargetUserId));
        Assert.Equal(Value(before, ImpersonationClaims.SessionId), Value(after, ImpersonationClaims.SessionId));
        await AssertRefusedAsync(browser, "/superadmin/overview");

        (await browser.PostFormAsync("/Account/Impersonation/End")).Dispose();
        (await browser.Http.PostAsync("/test/refresh-sign-in", content: null, cancellationToken)).Dispose();
        Assert.Equal([AppRoles.Admin], Roles(await browser.ClaimsAsync()));
    }

    [Fact]
    public async Task Starting_again_for_the_target_the_cookie_impersonates_reuses_its_open_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);

        (await StartAsync(browser, scenario.A.OwnerId)).Dispose();
        var first = Value(await browser.ClaimsAsync(), ImpersonationClaims.SessionId);

        using (var again = await StartAsync(browser, scenario.A.OwnerId))
        {
            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        }

        Assert.Equal(first, Value(await browser.ClaimsAsync(), ImpersonationClaims.SessionId));
        await using var check = this.database.CreateSystemContext();
        var rows = await check.ImpersonationAudits.Where(audit => audit.ImpersonatorId == platform.OwnerId).ToListAsync(cancellationToken);
        Assert.Equal(first, Assert.Single(rows).Id.ToString());
        Assert.Null(rows[0].Ended);
    }

    [Fact]
    public async Task A_start_whose_cookie_cannot_be_issued_leaves_no_open_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database, services => services.AddScoped<IAuthenticationService>(provider =>
            new RefusingImpersonationCookies(ActivatorUtilities.CreateInstance<AuthenticationService>(provider))));
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);

        try
        {
            using var started = await StartAsync(browser, scenario.A.OwnerId);
            Assert.NotEqual(HttpStatusCode.Redirect, started.StatusCode);
        }
        catch (InvalidOperationException exception) when (exception.Message == RefusingImpersonationCookies.Refusal)
        {
        }

        await using (var check = this.database.CreateSystemContext())
        {
            var row = Assert.Single(await check.ImpersonationAudits.Where(audit => audit.ImpersonatorId == platform.OwnerId).ToListAsync(cancellationToken));
            Assert.NotNull(row.Ended);
        }

        Assert.Equal([AppRoles.Admin], Roles(await browser.ClaimsAsync()));
    }

    [Fact]
    public async Task An_Admin_outside_the_platform_tenant_opens_its_own_tenant_pages_and_not_the_console()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.B.OwnerId, cancellationToken);
        await using var app = AppHost.Start(this.database);

        using var tenantAdmin = app.CreateBrowser();
        await tenantAdmin.SignInAsync(scenario.B.OwnerId);
        var claims = await tenantAdmin.ClaimsAsync();
        Assert.Contains(AppRoles.Admin, Roles(claims));
        Assert.DoesNotContain(claims, claim => claim.Type == SessionPolicies.PlatformTenantClaim);
        await AssertOpensAsync(tenantAdmin, "/tenant/management/users");
        await AssertRefusedAsync(tenantAdmin, "/superadmin/overview");
        await AssertRefusedAsync(tenantAdmin, "/superadmin/tenants");

        using var owner = app.CreateBrowser();
        await owner.SignInAsync(platform.OwnerId);
        Assert.Equal("true", Value(await owner.ClaimsAsync(), SessionPolicies.PlatformTenantClaim));
        await AssertOpensAsync(owner, "/superadmin/overview");
        await AssertRefusedAsync(owner, "/tenant/management/users");
    }

    [Fact]
    public async Task Logging_out_is_a_form_post_that_clears_the_cookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(scenario.A.OwnerId);

        Assert.NotEmpty(await browser.ClaimsAsync());

        using (var loggedOut = await browser.PostFormAsync("/Account/Logout", new() { ["returnUrl"] = string.Empty }))
        {
            Assert.Equal(HttpStatusCode.Redirect, loggedOut.StatusCode);
        }

        using var claims = await browser.Http.GetAsync("/test/claims", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, claims.StatusCode);
    }

    [Fact]
    public async Task Signing_out_while_impersonating_closes_the_impersonations_own_audit_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        (await StartAsync(browser, scenario.A.OwnerId)).Dispose();
        var row = await OpenRowAsync(platform.OwnerId, cancellationToken);
        Assert.NotNull(row);

        using (var loggedOut = await browser.PostFormAsync("/Account/Logout", new() { ["returnUrl"] = string.Empty }))
        {
            Assert.Equal(HttpStatusCode.Redirect, loggedOut.StatusCode);
        }

        using (var claims = await browser.Http.GetAsync("/test/claims", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, claims.StatusCode);
        }

        Assert.Null(await OpenRowAsync(platform.OwnerId, cancellationToken));
        await using var check = this.database.CreateSystemContext();
        Assert.NotNull((await check.ImpersonationAudits.AsNoTracking().SingleAsync(audit => audit.Id == row, cancellationToken)).Ended);
    }

    [Fact]
    public async Task The_app_maps_no_post_of_ones_own_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is null
                && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) == true)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(route => route is not null && route.StartsWith("/Account/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["/Account/Impersonation/End", "/Account/Impersonation/Start", "/Account/Logout"], endpoints);
    }

    private async Task<(TwoTenantScenario Scenario, ScenarioTenant Platform)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GiveIdentityRoleAsync(this.database, scenario.A.OwnerId, TargetRole, cancellationToken);
        return (scenario, platform);
    }

    private static Task<HttpResponseMessage> StartAsync(AppBrowser browser, Guid targetUserId) =>
        browser.PostFormAsync("/Account/Impersonation/Start", new() { ["targetUserId"] = targetUserId.ToString() });

    private static async Task AssertOpensAsync(AppBrowser browser, string path)
    {
        using var response = await browser.Http.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {response.Headers.Location}");
    }

    private static async Task AssertRefusedAsync(AppBrowser browser, string path)
    {
        using var response = await browser.Http.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{path}: {(int)response.StatusCode}");
        Assert.Contains("/Account/AccessDenied", response.Headers.Location?.OriginalString);
    }

    private static List<string> Roles(IEnumerable<CookieClaim> claims) => [.. claims.Where(claim => claim.Type == ClaimTypes.Role).Select(claim => claim.Value)];

    private static string? Value(IEnumerable<CookieClaim> claims, string type) => claims.SingleOrDefault(claim => claim.Type == type)?.Value;

    private async Task<Guid?> OpenRowAsync(Guid impersonatorId, CancellationToken cancellationToken)
    {
        await using var check = this.database.CreateSystemContext();
        return await check.ImpersonationAudits.Where(audit => audit.ImpersonatorId == impersonatorId && audit.Ended == null).Select(audit => (Guid?)audit.Id).SingleOrDefaultAsync(cancellationToken);
    }

    private sealed class RefusingImpersonationCookies : IAuthenticationService
    {
        public const string Refusal = "The impersonation cookie could not be issued.";

        private readonly IAuthenticationService inner;

        public RefusingImpersonationCookies(IAuthenticationService inner)
        {
            this.inner = inner;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => this.inner.AuthenticateAsync(context, scheme);

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => this.inner.ChallengeAsync(context, scheme, properties);

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => this.inner.ForbidAsync(context, scheme, properties);

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            ImpersonationClaims.IsPresent(principal) ? throw new InvalidOperationException(Refusal) : this.inner.SignInAsync(context, scheme, principal, properties);

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => this.inner.SignOutAsync(context, scheme, properties);
    }
}
