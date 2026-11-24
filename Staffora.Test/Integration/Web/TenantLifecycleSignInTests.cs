using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public partial class TenantLifecycleSignInTests : IClassFixture<RealDatabase>
{
    private const string Password = "Correct-Horse-7";
    private const string AuthCookie = "Staffora.Auth";

    private readonly RealDatabase database;

    public TenantLifecycleSignInTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_suspended_tenants_user_is_refused_at_password_sign_in_told_why_only_with_the_right_password_and_reactivation_lets_them_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        var email = await CreateUserAsync(app, tenant.TenantId, "suspended");

        using var signedIn = app.CreateBrowser();
        string issuedBefore;
        using (var first = await SignInAsync(signedIn, email, Password))
        {
            Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
            issuedBefore = AuthCookieOf(first)!;
            Assert.NotNull(issuedBefore);
        }

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(signedIn, "/test/claims")).Status);

        await MoveAsync(platform, tenant.TenantId, "tenant-suspend", cancellationToken);

        var stale = await GetAsync(signedIn, "/test/claims");
        Assert.Equal((HttpStatusCode.Unauthorized, true), (stale.Status, stale.Deleted));

        using var browser = app.CreateBrowser();
        using (var wrong = await SignInAsync(browser, email, "Wrong-password-1"))
        {
            var page = await PageTextAsync(wrong);
            Assert.Contains("Wrong email or password", page);
            Assert.DoesNotContain(ApplicationSignInManager.TenantNotActiveMessage, page);
            Assert.Null(AuthCookieOf(wrong));
        }

        Assert.Equal(1, await FailedCountAsync(app, email));

        using (var right = await SignInAsync(browser, email, Password))
        {
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
            Assert.Contains(ApplicationSignInManager.TenantNotActiveMessage, await PageTextAsync(right));
            Assert.Null(AuthCookieOf(right));
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-reactivate", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithCookieAsync(app, issuedBefore, "/test/claims"));
        using (var again = await SignInAsync(browser, email, Password))
        {
            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            var fresh = AuthCookieOf(again);
            Assert.NotNull(fresh);
            Assert.Equal(HttpStatusCode.OK, await StatusWithCookieAsync(app, fresh, "/test/claims"));
        }
    }

    [Fact]
    public async Task A_sign_in_records_when_the_user_signed_in_on_their_tenants_behalf_and_a_refused_one_records_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, _) = await TenantAndOwnerAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        var email = await CreateUserAsync(app, tenant.TenantId, "signedin");
        using var browser = app.CreateBrowser();

        using (var wrong = await SignInAsync(browser, email, "Wrong-password-1"))
        {
            Assert.Null(AuthCookieOf(wrong));
        }

        Assert.Null((await StoredUserAsync(email, cancellationToken)).LastSignedIn);

        var before = DateTime.UtcNow.AddSeconds(-5);
        using (var right = await SignInAsync(browser, email, Password))
        {
            Assert.NotNull(AuthCookieOf(right));
        }

        var stored = await StoredUserAsync(email, cancellationToken);
        Assert.NotNull(stored.LastSignedIn);
        Assert.InRange(stored.LastSignedIn!.Value, before, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(SystemAccount.IdFor(tenant.TenantId), stored.ModifiedById);
    }

    private async Task<ApplicationUser> StoredUserAsync(string email, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a user as stored");
        return await system.Users.AsNoTracking().SingleAsync(user => user.Email == email, cancellationToken);
    }

    [Fact]
    public async Task No_cookie_is_issued_for_a_user_of_a_suspended_tenant_whichever_path_asks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        var email = await CreateUserAsync(app, tenant.TenantId, "choke");
        await MoveAsync(platform, tenant.TenantId, "tenant-suspend", cancellationToken);

        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        Assert.Equal("The user may not sign in now.", (await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.SignInAsync(user, isPersistent: false))).Message);
        Assert.Equal("The user may not sign in now.", (await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.SignInWithClaimsAsync(user, isPersistent: false, []))).Message);
    }

    [Fact]
    public async Task A_tenant_scheduled_for_deletion_and_then_archived_refuses_sign_in_and_every_cookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        var email = await CreateUserAsync(app, tenant.TenantId, "closing");
        using var signedIn = app.CreateBrowser();
        using (var first = await SignInAsync(signedIn, email, Password))
        {
            Assert.NotNull(AuthCookieOf(first));
        }

        foreach (var step in new[] { "tenant-schedule-deletion", "tenant-archive" })
        {
            await MoveAsync(platform, tenant.TenantId, step, cancellationToken);

            using var browser = app.CreateBrowser();
            using var right = await SignInAsync(browser, email, Password);
            Assert.Contains(ApplicationSignInManager.TenantNotActiveMessage, await PageTextAsync(right));
            Assert.Null(AuthCookieOf(right));
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(signedIn, "/test/claims")).Status);
        }
    }

    private async Task<(ScenarioTenant Tenant, ScenarioTenant Platform)> TenantAndOwnerAsync(CancellationToken cancellationToken)
    {
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Company Lifecycle"), cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        return (tenant, platform);
    }

    private async Task MoveAsync(ScenarioTenant platform, Guid tenantId, string step, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId));
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, step, cancellationToken: cancellationToken);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private static async Task<string> CreateUserAsync(AppHost app, Guid tenantId, string name)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var email = $"{name}.{Guid.NewGuid():N}@company-lifecycle.example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = name,
            LastName = "Tester",
            EmailConfirmed = true,
            State = UserState.Active,
            TenantId = tenantId,
            CreatedById = SystemAccount.IdFor(tenantId),
        };

        var created = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(error => error.Description)));
        return email;
    }

    private static async Task<HttpStatusCode> StatusWithCookieAsync(AppHost app, string cookie, string path)
    {
        using var browser = app.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        using var response = await browser.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<int> FailedCountAsync(AppHost app, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.GetAccessFailedCountAsync((await users.FindByEmailAsync(email))!);
    }

    private static Task<HttpResponseMessage> SignInAsync(AppBrowser browser, string email, string password) =>
        PostLoginAsync(browser, new() { ["Input.Email"] = email, ["Input.Password"] = password });

    private static async Task<HttpResponseMessage> PostLoginAsync(AppBrowser browser, Dictionary<string, string> fields)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var viewed = await browser.Http.GetAsync("/Account/Login", cancellationToken);
        var token = AntiforgeryField().Match(await viewed.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(token.Success, "The sign-in page carries no antiforgery token.");
        var form = new Dictionary<string, string>(fields)
        {
            ["_handler"] = "login",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups["value"].Value),
        };
        return await browser.Http.PostAsync("/Account/Login", new FormUrlEncodedContent(form), cancellationToken);
    }

    private static async Task<(HttpStatusCode Status, bool Deleted)> GetAsync(AppBrowser browser, string path)
    {
        using var response = await browser.Http.GetAsync(path, TestContext.Current.CancellationToken);
        var deleted = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith(AuthCookie + "=;", StringComparison.Ordinal) || (cookie.StartsWith(AuthCookie, StringComparison.Ordinal) && cookie.Contains("1970", StringComparison.Ordinal)));
        return (response.StatusCode, deleted);
    }

    private static async Task<string> PageTextAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static string? AuthCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(cookie => cookie.Split(';')[0]).FirstOrDefault(cookie => cookie.StartsWith(AuthCookie, StringComparison.Ordinal) && !cookie.EndsWith('='))
            : null;

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*?value=\"(?<value>[^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
