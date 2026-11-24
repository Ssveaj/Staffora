using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Services.Authorization;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class ImpersonationEndpointTests : IClassFixture<RealDatabase>
{
    private const string TargetRole = "Coordinator role of A";

    private readonly RealDatabase database;

    public ImpersonationEndpointTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Starting_puts_the_targets_roles_on_the_cookie_and_ending_puts_the_owners_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        Assert.Equal([AppRoles.Admin], Roles(await browser.ClaimsAsync()));

        using (var started = await StartAsync(browser, scenario.A.OwnerId))
        {
            Assert.Equal((HttpStatusCode.Redirect, "/"), (started.StatusCode, started.Headers.Location?.OriginalString));
        }

        var impersonating = await browser.ClaimsAsync();
        var row = await OpenRowAsync(platform.OwnerId, cancellationToken);
        Assert.Equal([TargetRole], Roles(impersonating));
        Assert.Equal((platform.OwnerId.ToString(), scenario.A.OwnerId.ToString(), row.ToString()), (Value(impersonating, ClaimTypes.NameIdentifier), Value(impersonating, ImpersonationClaims.TargetUserId), Value(impersonating, ImpersonationClaims.SessionId)));

        using (var ended = await browser.PostFormAsync("/Account/Impersonation/End"))
        {
            Assert.Equal((HttpStatusCode.Redirect, "/superadmin/overview"), (ended.StatusCode, ended.Headers.Location?.OriginalString));
        }

        var back = await browser.ClaimsAsync();
        Assert.Equal([AppRoles.Admin], Roles(back));
        Assert.DoesNotContain(back, claim => claim.Type is ImpersonationClaims.TargetUserId or ImpersonationClaims.SessionId);
        Assert.Null(await OpenRowAsync(platform.OwnerId, cancellationToken));
    }

    [Fact]
    public async Task The_platform_console_is_refused_while_impersonating_and_opens_again_after_the_end()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        await AssertOpensAsync(browser, "/superadmin/overview");

        (await StartAsync(browser, scenario.A.OwnerId)).Dispose();
        foreach (var console in new[] { "/superadmin/overview", "/superadmin/tenants", $"/superadmin/tenants/{scenario.A.TenantId}", $"/superadmin/tenants/{scenario.A.TenantId}/users", "/superadmin/settings", "/superadmin/errorlog" })
        {
            await AssertRefusedAsync(browser, console);
        }

        (await browser.PostFormAsync("/Account/Impersonation/End")).Dispose();
        await AssertOpensAsync(browser, "/superadmin/overview");
    }

    [Fact]
    public async Task A_tenant_page_is_refused_to_a_platform_owner_who_is_not_impersonating_and_opens_while_impersonating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var owner = app.CreateBrowser();
        await owner.SignInAsync(platform.OwnerId);

        foreach (var tenantPage in new[] { "/tenant/management", "/tenant/management/users", "/me/overview" })
        {
            await AssertRefusedAsync(owner, tenantPage);
        }

        (await StartAsync(owner, scenario.A.OwnerId)).Dispose();
        await AssertOpensAsync(owner, "/tenant/management/users");

        using var tenantUser = app.CreateBrowser();
        await tenantUser.SignInAsync(scenario.B.OwnerId);
        await AssertOpensAsync(tenantUser, "/tenant/management/users");
        await AssertRefusedAsync(tenantUser, "/superadmin/overview");
    }

    [Fact]
    public async Task The_endpoints_refuse_a_post_without_a_valid_antiforgery_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);

        using var other = app.CreateBrowser();
        await other.SignInAsync(scenario.B.OwnerId);
        using var foreignTokenResponse = await other.Http.GetAsync("/test/antiforgery", cancellationToken);
        var foreignToken = await foreignTokenResponse.Content.ReadAsStringAsync(cancellationToken);

        using (var withoutToken = await browser.PostFormAsync("/Account/Impersonation/Start", Target(scenario.A.OwnerId), withAntiforgery: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        }

        (await browser.Http.GetAsync("/test/antiforgery", cancellationToken)).Dispose();
        using (var foreign = await browser.PostFormAsync("/Account/Impersonation/Start", new() { ["targetUserId"] = scenario.A.OwnerId.ToString(), ["__RequestVerificationToken"] = foreignToken }, withAntiforgery: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        }

        Assert.Null(await OpenRowAsync(platform.OwnerId, cancellationToken));
        Assert.Equal([AppRoles.Admin], Roles(await browser.ClaimsAsync()));

        (await StartAsync(browser, scenario.A.OwnerId)).Dispose();
        using (var endWithoutToken = await browser.PostFormAsync("/Account/Impersonation/End", withAntiforgery: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, endWithoutToken.StatusCode);
        }

        Assert.Equal([TargetRole], Roles(await browser.ClaimsAsync()));
        Assert.NotNull(await OpenRowAsync(platform.OwnerId, cancellationToken));
    }

    [Fact]
    public async Task The_endpoints_forbid_a_user_who_is_not_a_platform_owner_and_need_a_signed_in_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var tenantUser = app.CreateBrowser();
        await tenantUser.SignInAsync(scenario.A.OwnerId);
        var before = await tenantUser.ClaimsAsync();

        foreach (var post in new[] { ("/Account/Impersonation/Start", Target(scenario.B.OwnerId)), ("/Account/Impersonation/End", new Dictionary<string, string>()) })
        {
            using var response = await tenantUser.PostFormAsync(post.Item1, post.Item2);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/Account/AccessDenied", response.Headers.Location?.PathAndQuery ?? response.Headers.Location?.OriginalString);
        }

        Assert.Equal(before.Select(claim => (claim.Type, claim.Value)), (await tenantUser.ClaimsAsync()).Select(claim => (claim.Type, claim.Value)));
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.ImpersonationAudits.AnyAsync(audit => audit.ImpersonatorId == scenario.A.OwnerId, cancellationToken));
        }

        using var anonymous = app.CreateBrowser();
        using var challenged = await anonymous.PostFormAsync("/Account/Impersonation/Start", Target(scenario.B.OwnerId));
        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);
        Assert.Contains("/Account/Login", challenged.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_impersonation_survives_the_security_stamp_refresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform) = await ArrangeAsync(cancellationToken);

        await using var app = AppHost.Start(this.database, services => services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero));
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        (await StartAsync(browser, scenario.A.OwnerId)).Dispose();

        foreach (var refresh in new[] { "first refresh", "second refresh" })
        {
            var claims = await browser.ClaimsAsync();
            Assert.True(Roles(claims).SequenceEqual([TargetRole]), $"{refresh}: {string.Join(", ", Roles(claims))}");
            Assert.Equal(scenario.A.OwnerId.ToString(), Value(claims, ImpersonationClaims.TargetUserId));
        }

        await AssertRefusedAsync(browser, "/superadmin/overview");
    }

    private async Task<(TwoTenantScenario Scenario, ScenarioTenant Platform)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GiveIdentityRoleAsync(this.database, scenario.A.OwnerId, TargetRole, cancellationToken);
        return (scenario, platform);
    }

    private static Task<HttpResponseMessage> StartAsync(AppBrowser browser, Guid targetUserId) =>
        browser.PostFormAsync("/Account/Impersonation/Start", Target(targetUserId));

    private static Dictionary<string, string> Target(Guid targetUserId) => new() { ["targetUserId"] = targetUserId.ToString() };

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
}
