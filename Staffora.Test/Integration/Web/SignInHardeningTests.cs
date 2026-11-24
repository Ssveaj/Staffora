using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Identity;
using Staffora.Services.Diagnostics;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public partial class SignInHardeningTests : IClassFixture<RealDatabase>
{
    private const string Password = "Correct-Horse-7";

    private readonly RealDatabase database;

    public SignInHardeningTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Five_wrong_passwords_in_the_sign_in_form_lock_the_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        var email = await CreateUserAsync(app, scenario.A.TenantId, "locked");
        using var browser = app.CreateBrowser();

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await SignInAsync(browser, email, $"Wrong-password-{attempt}");
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Contains("Wrong email or password", await wrong.Content.ReadAsStringAsync(cancellationToken));
            Assert.False(await IsLockedOutAsync(app, email));
        }

        using (var fifth = await SignInAsync(browser, email, "Wrong-password-5"))
        {
            Assert.Equal(HttpStatusCode.Redirect, fifth.StatusCode);
            Assert.Contains("Account/Lockout", fifth.Headers.Location?.OriginalString);
        }

        Assert.True(await IsLockedOutAsync(app, email));

        using var right = await SignInAsync(browser, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        Assert.Contains("Account/Lockout", right.Headers.Location?.OriginalString);
        Assert.DoesNotContain(CookiesOf(right), cookie => cookie.StartsWith("Staffora.Auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_right_password_issues_an_http_only_secure_lax_cookie_of_eight_sliding_hours()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        var email = await CreateUserAsync(app, scenario.A.TenantId, "cookie");
        using var browser = app.CreateBrowser();

        using var signedIn = await SignInAsync(browser, email, Password);

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var cookie = Assert.Single(CookiesOf(signedIn), value => value.StartsWith("Staffora.Auth=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("; httponly", cookie);
        Assert.Contains("; secure", cookie);
        Assert.Contains("; samesite=lax", cookie);

        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        Assert.Equal(TimeSpan.FromHours(8), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    [Fact]
    public async Task A_page_response_carries_the_security_headers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();

        using var page = await browser.Http.GetAsync("/Account/Login", cancellationToken);
        var html = await page.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(["nosniff"], page.Headers.GetValues(SecurityHeaders.ContentTypeOptionsHeader));
        Assert.Equal(["same-origin"], page.Headers.GetValues(SecurityHeaders.ReferrerPolicyHeader));

        var policy = Assert.Single(page.Headers.GetValues(SecurityHeaders.ContentSecurityPolicyHeader));
        var directives = policy.Split(';', StringSplitOptions.TrimEntries).ToDictionary(directive => directive.Split(' ')[0], directive => directive);
        Assert.Equal("frame-ancestors 'none'", directives["frame-ancestors"]);
        Assert.Equal("default-src 'self'", directives["default-src"]);
        Assert.Equal("object-src 'none'", directives["object-src"]);
        var nonce = NonceInPolicy().Match(directives["script-src"]).Groups["nonce"].Value;
        Assert.Matches(NonceInPolicy(), directives["script-src"]);
        Assert.DoesNotContain("unsafe", directives["script-src"]);
        Assert.Equal($"script-src-elem 'self' 'nonce-{nonce}'", directives["script-src-elem"]);
        Assert.Equal("script-src-attr 'unsafe-inline'", directives["script-src-attr"]);

        Assert.Contains($"<script type=\"importmap\" nonce=\"{nonce}\">", html);
        using var again = await browser.Http.GetAsync("/Account/Login", cancellationToken);
        Assert.DoesNotContain(nonce, Assert.Single(again.Headers.GetValues(SecurityHeaders.ContentSecurityPolicyHeader)));
    }

    [Fact]
    public async Task Sign_in_posts_beyond_the_limit_are_answered_429_while_viewing_the_page_is_not_limited()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = AppHost.Start(this.database);
        var limit = app.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.SignIn.PermitLimit;
        using var browser = app.CreateBrowser();

        for (var attempt = 1; attempt <= limit; attempt++)
        {
            using var allowed = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));

        using var view = await browser.Http.GetAsync("/Account/Login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
    }

    [Fact]
    public async Task Impersonation_posts_beyond_the_limit_are_answered_429()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        var limit = app.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.Impersonation.PermitLimit;
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);

        for (var post = 1; post <= limit; post++)
        {
            using var allowed = await browser.PostFormAsync("/Account/Impersonation/End");
            Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
        }

        using var refused = await browser.PostFormAsync("/Account/Impersonation/End");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Theory]
    [InlineData("/Account/ForgotPassword")]
    [InlineData("/Account/ForgotPasswordConfirmation")]
    [InlineData("/Account/ResetPassword")]
    [InlineData("/Account/ResetPasswordConfirmation")]
    [InlineData("/Account/InvalidPasswordReset")]
    [InlineData("/Account/ConfirmEmail")]
    [InlineData("/Account/ConfirmEmailChange")]
    [InlineData("/Account/ResendEmailConfirmation")]
    [InlineData("/Account/Manage/Email")]
    public async Task The_e_mail_flows_are_not_reachable(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);

        using var anonymous = app.CreateBrowser();
        using (var response = await anonymous.Http.GetAsync(path, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var signedIn = app.CreateBrowser();
        await signedIn.SignInAsync(platform.OwnerId);
        using (var response = await signedIn.Http.GetAsync(path, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task No_e_mail_sender_is_registered_and_the_sign_in_page_offers_no_e_mail_flow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = AppHost.Start(this.database);

        Assert.Null(app.Services.GetService<IEmailSender<ApplicationUser>>());

        using var browser = app.CreateBrowser();
        var html = await browser.Http.GetStringAsync("/Account/Login", cancellationToken);
        Assert.DoesNotContain("ForgotPassword", html);
        Assert.DoesNotContain("ResendEmailConfirmation", html);
    }

    [Fact]
    public async Task Pages_need_a_signed_in_user_unless_they_allow_anonymous_use()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();

        using (var closed = await browser.Http.GetAsync("/", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
            Assert.Contains("/Account/Login", closed.Headers.Location?.OriginalString);
        }

        foreach (var path in new[] { "/Account/Login", "/Account/Lockout", "/Account/AccessDenied", "/not-found", "/Error" })
        {
            using var open = await browser.Http.GetAsync(path, cancellationToken);
            Assert.True(open.StatusCode == HttpStatusCode.OK, $"{path} answered {open.StatusCode}");
        }

        using var hub = await browser.Http.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
    }

    [Fact]
    public async Task A_user_with_two_factor_sign_in_on_is_signed_in_by_the_password_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        var email = await CreateUserAsync(app, scenario.A.TenantId, "second-factor");
        await EnableTwoFactorAsync(app, email);
        using var browser = app.CreateBrowser();

        using var password = await SignInAsync(browser, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, password.StatusCode);
        Assert.DoesNotContain("LoginWith", password.Headers.Location?.OriginalString);
        Assert.Contains(CookiesOf(password), cookie => cookie.StartsWith("Staffora.Auth=", StringComparison.Ordinal));
        Assert.DoesNotContain(CookiesOf(password), cookie => cookie.StartsWith("Identity.TwoFactorUserId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_address_costs_the_same_password_verification_as_a_known_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var hasher = new CountingPasswordHasher();
        await using var app = AppHost.Start(this.database, services => services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher));
        var email = await CreateUserAsync(app, scenario.A.TenantId, "timing");
        using var browser = app.CreateBrowser();

        var before = hasher.Verifications;
        using (var unknown = await SignInAsync(browser, $"nobody.{Guid.NewGuid():N}@company-a.example.com", "Wrong-password-1"))
        {
            Assert.Contains("Wrong email or password", await unknown.Content.ReadAsStringAsync(cancellationToken));
        }

        var forUnknown = hasher.Verifications - before;
        before = hasher.Verifications;
        using (var known = await SignInAsync(browser, email, "Wrong-password-1"))
        {
            Assert.Contains("Wrong email or password", await known.Content.ReadAsStringAsync(cancellationToken));
        }

        var forKnown = hasher.Verifications - before;
        Assert.Equal(1, forKnown);
        Assert.Equal(forKnown, forUnknown);
    }

    [Fact]
    public async Task The_forwarded_headers_switch_alone_trusts_no_proxy_so_a_rotating_forwarded_address_is_still_limited()
    {
        await using var app = AppHost.Start(this.database.ConnectionString, "Test", new Dictionary<string, string>
        {
            ["RateLimits:SignIn:PermitLimit"] = "2",
            ["RateLimits:SignIn:Window"] = "00:01:00",
            ["FORWARDEDHEADERS_ENABLED"] = "true",
        });
        using var browser = app.CreateBrowser();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var response = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1", forwardedFor: $"203.0.113.{attempt}");
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task A_configured_proxy_gives_each_client_its_own_limit_and_an_unknown_sender_cannot_choose_one()
    {
        var settings = new Dictionary<string, string>
        {
            ["RateLimits:SignIn:PermitLimit"] = "2",
            ["RateLimits:SignIn:Window"] = "00:01:00",
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.100",
        };

        await using (var app = AppHost.Start(this.database.ConnectionString, "Test", settings, services => services.AddSingleton<IStartupFilter>(new ClientAddress("10.0.0.100"))))
        {
            using var browser = app.CreateBrowser();
            for (var client = 1; client <= 3; client++)
            {
                using var response = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1", forwardedFor: $"203.0.113.{client}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using var third = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1", forwardedFor: "203.0.113.1");
            using var fourth = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1", forwardedFor: "203.0.113.1");
            Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        }

        await using (var app = AppHost.Start(this.database.ConnectionString, "Test", settings, services => services.AddSingleton<IStartupFilter>(new ClientAddress("10.0.0.200"))))
        {
            using var browser = app.CreateBrowser();
            var statuses = new List<HttpStatusCode>();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using var response = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1", forwardedFor: $"203.0.113.{attempt}");
                statuses.Add(response.StatusCode);
            }

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        }
    }

    [Fact]
    public async Task The_antiforgery_cookie_is_secure_and_always_so_outside_development()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();

        using var page = await browser.Http.GetAsync("/Account/Login", cancellationToken);

        var cookie = Assert.Single(CookiesOf(page), value => value.StartsWith("Staffora.Antiforgery=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("; secure", cookie);
        Assert.Contains("; httponly", cookie);
        Assert.Equal(CookieSecurePolicy.Always, app.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task A_post_without_a_form_body_gets_the_status_page_of_its_code_not_a_server_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = AppHost.Start(this.database.ConnectionString, "Test", new Dictionary<string, string>
        {
            ["RateLimits:SignIn:PermitLimit"] = "1",
            ["RateLimits:SignIn:Window"] = "00:01:00",
        });
        using var browser = app.CreateBrowser();

        using (var allowed = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        HttpRequestMessage Bodyless(HttpContent? content) => new(HttpMethod.Post, "/Account/Login") { Content = content };

        using (var second = Bodyless(content: null))
        using (var limited = await browser.Http.SendAsync(second, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.Contains("Retry-After"));
            Assert.Contains("Too many attempts", await limited.Content.ReadAsStringAsync(cancellationToken));
        }

        using (var third = Bodyless(JsonBody()))
        using (var json = await browser.Http.SendAsync(third, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, json.StatusCode);
            Assert.Contains("Too many attempts", await json.Content.ReadAsStringAsync(cancellationToken));
        }

        foreach (var content in new HttpContent?[] { null, JsonBody() })
        {
            using var unknown = await browser.Http.PostAsync("/no-such-page", content, cancellationToken);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, unknown.StatusCode);
            Assert.Contains("Something went wrong", await unknown.Content.ReadAsStringAsync(cancellationToken));
        }

        static HttpContent JsonBody() => new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("Development")]
    public async Task A_post_without_a_valid_antiforgery_token_is_refused_400_with_the_status_page_and_leaves_no_error_log_row(string environment)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using var app = AppHost.Start(this.database.ConnectionString, environment, new Dictionary<string, string> { [StartupSeeding.SkipSetting] = "true" });
        using var browser = app.CreateBrowser();
        using var signedIn = app.CreateBrowser();
        await signedIn.SignInAsync(scenario.A.OwnerId);
        var correlationId = $"test-{Guid.NewGuid():N}";

        async Task<HttpResponseMessage> PostAsync(AppBrowser from, string path, string? headerToken, HttpContent? content)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            request.Headers.Add(LogScope.CorrelationHeader, correlationId);
            if (headerToken is not null)
            {
                request.Headers.Add("RequestVerificationToken", headerToken);
            }

            return await from.Http.SendAsync(request, cancellationToken);
        }

        async Task AssertRefusedAsync(HttpResponseMessage response, bool statusPage = true)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            if (statusPage)
            {
                Assert.Contains("Something went wrong", await response.Content.ReadAsStringAsync(cancellationToken));
            }
        }

        using (var refused = await PostAsync(signedIn, "/Account/Logout", headerToken: null, new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "" })))
        {
            await AssertRefusedAsync(refused);
        }

        var form = new Dictionary<string, string> { ["_handler"] = "login", ["Input.Email"] = "nobody@example.com", ["Input.Password"] = "Wrong-password-1", ["__RequestVerificationToken"] = "not-a-token" };
        using (var refused = await PostAsync(browser, "/Account/Login", headerToken: null, new FormUrlEncodedContent(form)))
        {
            await AssertRefusedAsync(refused, statusPage: environment != "Development");
        }

        using (var signIn = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1"))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            Assert.Contains("Wrong email or password", await signIn.Content.ReadAsStringAsync(cancellationToken));
        }

        await app.Services.GetRequiredService<ErrorLogQueue>().FlushAsync(cancellationToken);
        await using var check = this.database.CreateSystemContext();
        Assert.Empty(await check.ErrorLogs.Where(log => log.CorrelationId == correlationId).Select(log => log.Message).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Error_statuses_are_answered_by_the_status_page_of_their_code()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database.ConnectionString, "Test", new Dictionary<string, string>
        {
            ["RateLimits:SignIn:PermitLimit"] = "1",
            ["RateLimits:SignIn:Window"] = "00:01:00",
        });
        using var browser = app.CreateBrowser();

        (await SignInAsync(browser, "nobody@example.com", "Wrong-password-1")).Dispose();
        using (var limited = await SignInAsync(browser, "nobody@example.com", "Wrong-password-1"))
        {
            var body = await limited.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Contains("Too many attempts", body);
            Assert.Contains("try again in a minute", body);
            Assert.DoesNotContain("does not exist", body);
        }

        using (var unknown = await browser.Http.GetAsync("/no-such-page", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Contains("Not found", await unknown.Content.ReadAsStringAsync(cancellationToken));
        }

        using var signedIn = app.CreateBrowser();
        await signedIn.SignInAsync(platform.OwnerId);
        using (var unknown = await signedIn.Http.GetAsync("/no-such-page", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Contains("Not found", await unknown.Content.ReadAsStringAsync(cancellationToken));
        }

        using (var refused = await signedIn.PostFormAsync("/Account/Impersonation/End", withAntiforgery: false))
        {
            var body = await refused.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Something went wrong", body);
            Assert.DoesNotContain("does not exist", body);
        }
    }

    private static async Task<string> CreateUserAsync(AppHost app, Guid tenantId, string name)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var email = $"{name}.{Guid.NewGuid():N}@company-a.example.com";
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

    private static async Task<bool> IsLockedOutAsync(AppHost app, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.IsLockedOutAsync((await users.FindByEmailAsync(email))!);
    }

    private static Task<HttpResponseMessage> SignInAsync(AppBrowser browser, string email, string password, string? forwardedFor = null) =>
        PostPageAsync(browser, "/Account/Login", "login", new() { ["Input.Email"] = email, ["Input.Password"] = password }, forwardedFor);

    private static async Task<HttpResponseMessage> PostPageAsync(AppBrowser browser, string page, string handler, Dictionary<string, string> fields, string? forwardedFor = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var view = new HttpRequestMessage(HttpMethod.Get, page);
        using var post = new HttpRequestMessage(HttpMethod.Post, page);
        if (forwardedFor is not null)
        {
            view.Headers.Add("X-Forwarded-For", forwardedFor);
            post.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        using var viewed = await browser.Http.SendAsync(view, cancellationToken);
        var token = AntiforgeryField().Match(await viewed.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(token.Success, $"{page} carries no antiforgery token.");

        post.Content = new FormUrlEncodedContent(new Dictionary<string, string>(fields)
        {
            ["_handler"] = handler,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups["value"].Value),
        });
        return await browser.Http.SendAsync(post, cancellationToken);
    }

    private static async Task EnableTwoFactorAsync(AppHost app, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
        Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
    }

    private sealed class CountingPasswordHasher : IPasswordHasher<ApplicationUser>
    {
        private readonly PasswordHasher<ApplicationUser> inner = new();
        private int verifications;

        public int Verifications => Volatile.Read(ref this.verifications);

        public string HashPassword(ApplicationUser user, string password) => this.inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref this.verifications);
            return this.inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class ClientAddress : IStartupFilter
    {
        private readonly string address;

        public ClientAddress(string address)
        {
            this.address = address;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(this.address);
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private static IEnumerable<string> CookiesOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [];

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*?value=\"(?<value>[^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    [GeneratedRegex("'nonce-(?<nonce>[A-Za-z0-9+/=]+)'")]
    private static partial Regex NonceInPolicy();
}
