using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Core.Entities.Identity;
using Staffora.Data;

namespace Staffora.Test.Integration.Infrastructure;

public sealed class AppHost : IAsyncDisposable
{
    private readonly Factory factory;

    private AppHost(Factory factory) => this.factory = factory;

    public IServiceProvider Services => this.factory.Services;

    public TestServer Server => this.factory.Server;

    public static AppHost Start(RealDatabase database, Action<IServiceCollection>? configure = null) =>
        Start(database.ConnectionString, "Test", settings: null, configure);

    public static AppHost Start(string connectionString, string environment, IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var factory = new Factory(connectionString, environment, settings ?? new Dictionary<string, string>(), configure);
        _ = factory.Server;
        return new AppHost(factory);
    }

    public static AppHost StartOnKestrel(string connectionString, string environment, int port, IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var factory = new Factory(connectionString, environment, settings ?? new Dictionary<string, string>(), configure);
        factory.UseKestrel(options => options.ListenLocalhost(port));
        factory.StartServer();
        try
        {
            EnsureLoopback(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? []);
        }
        catch
        {
            factory.Dispose();
            throw;
        }

        return new AppHost(factory);
    }

    public static void EnsureLoopback(IEnumerable<string> addresses)
    {
        var listed = addresses.ToList();
        var open = listed.Where(address => !IsLoopbackAddress(address)).ToList();
        if (listed.Count == 0 || open.Count > 0)
        {
            throw new InvalidOperationException(listed.Count == 0
                ? "The test host reports no address it listens on, so it cannot be shown to listen on the loopback address only."
                : $"The test host listens on {string.Join(", ", open)}, which other machines can reach; it serves routes that sign anyone in, so it listens on the loopback address only.");
        }
    }

    private static bool IsLoopbackAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    public static bool IsLoopbackCaller(HttpContext context) =>
        context.Connection.RemoteIpAddress is not { } remote || IPAddress.IsLoopback(remote);

    public AppBrowser CreateBrowser() =>
        new(this.factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") }));

    public ValueTask DisposeAsync() => this.factory.DisposeAsync();

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string connectionString;
        private readonly string environment;
        private readonly IReadOnlyDictionary<string, string> settings;
        private readonly Action<IServiceCollection>? configure;

        public Factory(string connectionString, string environment, IReadOnlyDictionary<string, string> settings, Action<IServiceCollection>? configure)
        {
            this.connectionString = connectionString;
            this.environment = environment;
            this.settings = settings;
            this.configure = configure;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(this.environment);
            builder.UseSetting("ConnectionStrings:DefaultConnection", this.connectionString);

            builder.UseSetting("JobClock:Enabled", "false");

            builder.UseSetting("IntegrationOutbox:Enabled", "false");

            builder.UseSetting("Retention:Enabled", "false");
            foreach (var (key, value) in this.settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddSingleton(ApplicationDbContextOptions.Create(this.connectionString, throwOnUnorderedRowLimit: true));
                services.AddSingleton<IStartupFilter, TestRoutes>();
                this.configure?.Invoke(services);
            });
        }
    }

    private sealed class TestRoutes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.StartsWith("/test/", StringComparison.Ordinal) && !IsLoopbackCaller(context))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (path.StartsWith("/test/sign-in/", StringComparison.Ordinal))
                {
                    var users = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                    var user = await users.FindByIdAsync(path["/test/sign-in/".Length..]);
                    if (user is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>().SignInAsync(user, isPersistent: false);
                    return;
                }

                if (path == "/test/antiforgery")
                {
                    if ((await context.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Principal is { } user)
                    {
                        context.User = user;
                    }

                    var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
                    await context.Response.WriteAsync(tokens.RequestToken!);
                    return;
                }

                if (path == "/test/refresh-sign-in")
                {
                    var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                    var users = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                    if (cookie.Principal is null || await users.GetUserAsync(cookie.Principal) is not { } user)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    context.User = cookie.Principal;
                    await context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>().RefreshSignInAsync(user);
                    return;
                }

                if (path == "/test/claims")
                {
                    var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                    if (cookie.Principal is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    await context.Response.WriteAsJsonAsync(cookie.Principal.Claims.Select(claim => new CookieClaim(claim.Type, claim.Value)));
                    return;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

public sealed record CookieClaim(string Type, string Value);

public sealed class AppBrowser : IDisposable
{
    private readonly HttpClient http;

    public AppBrowser(HttpClient http)
    {
        this.http = http;
    }

    public HttpClient Http => this.http;

    public async Task SignInAsync(Guid userId)
    {
        using var response = await this.http.PostAsync($"/test/sign-in/{userId}", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async Task<IReadOnlyList<CookieClaim>> ClaimsAsync()
    {
        using var response = await this.http.GetAsync("/test/claims", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<CookieClaim>>(TestContext.Current.CancellationToken))!;
    }

    public async Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string>? fields = null, bool withAntiforgery = true)
    {
        var form = new Dictionary<string, string>(fields ?? []);
        if (withAntiforgery)
        {
            using var tokenResponse = await this.http.GetAsync("/test/antiforgery", TestContext.Current.CancellationToken);
            form["__RequestVerificationToken"] = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        return await this.http.PostAsync(path, new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    public void Dispose() => this.http.Dispose();
}
