using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Platform;
using Staffora.Services.Diagnostics;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class ErrorLogRequestTests : IClassFixture<RealDatabase>
{
    private const string ThrowingPath = "/test/throw";

    private readonly RealDatabase database;

    public ErrorLogRequestTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task An_unhandled_exception_is_stored_with_its_requests_correlation_id_and_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var app = StartWithThrowingRoute();
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(scenario.A.OwnerId);
        var correlationId = $"test-{Guid.NewGuid():N}";

        using (var response = await GetThrowingAsync(browser, correlationId))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(correlationId, response.Headers.GetValues(LogScope.CorrelationHeader).Single());

            var page = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Matches($"<code data-correlation-id[^>]*>{correlationId}</code>", page);

            Assert.Contains("aria-label=\"Main navigation\"", page);
            Assert.DoesNotContain("Loading your session", page);
        }

        var row = await StoredErrorAsync(app, correlationId, cancellationToken);
        Assert.Equal((scenario.A.TenantId, scenario.A.OwnerId, (Guid?)null), (row.TenantId, row.UserId, row.ImpersonatorId));
        Assert.Equal((ThrowingPath, ErrorLogLevel.Error), (row.RequestPath, row.Level));
        Assert.Contains(ThrowingRoute.Message, row.ExceptionDetail);
    }

    [Fact]
    public async Task While_impersonating_the_row_names_the_impersonated_users_tenant_and_the_platform_owner_behind_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = StartWithThrowingRoute();
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        using (var started = await browser.PostFormAsync("/Account/Impersonation/Start", new() { ["targetUserId"] = scenario.B.OwnerId.ToString() }))
        {
            Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
        }

        var correlationId = $"test-{Guid.NewGuid():N}";
        (await GetThrowingAsync(browser, correlationId)).Dispose();

        var row = await StoredErrorAsync(app, correlationId, cancellationToken);
        Assert.Equal((scenario.B.TenantId, scenario.B.OwnerId, platform.OwnerId), (row.TenantId, row.UserId, row.ImpersonatorId));
    }

    [Fact]
    public async Task A_request_without_a_usable_correlation_id_gets_one_and_its_error_carries_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = StartWithThrowingRoute();
        using var browser = app.CreateBrowser();

        foreach (var sent in new string?[] { null, new('x', LogScope.MaxCorrelationIdLength + 1), "two words;and more" })
        {
            string returned;
            using (var response = await GetThrowingAsync(browser, sent))
            {
                returned = response.Headers.GetValues(LogScope.CorrelationHeader).Single();
            }

            Assert.NotEqual(sent, returned);
            Assert.Matches("^[0-9a-f]{32}$", returned);
            var row = await StoredErrorAsync(app, returned, cancellationToken);
            Assert.Equal(((Guid?)null, (Guid?)null), (row.TenantId, row.UserId));
        }
    }

    [Fact]
    public async Task The_error_log_page_opens_to_a_platform_owner_and_is_refused_to_a_tenant_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);

        using var owner = app.CreateBrowser();
        await owner.SignInAsync(platform.OwnerId);
        using (var opened = await owner.Http.GetAsync("/superadmin/errorlog", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        }

        using var tenantUser = app.CreateBrowser();
        await tenantUser.SignInAsync(scenario.A.OwnerId);
        using var refused = await tenantUser.Http.GetAsync("/superadmin/errorlog", cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("/Account/AccessDenied", refused.Headers.Location?.OriginalString);
    }

    private AppHost StartWithThrowingRoute() => AppHost.Start(this.database, services => services.AddSingleton<IStartupFilter, ThrowingRoute>());

    private static async Task<HttpResponseMessage> GetThrowingAsync(AppBrowser browser, string? correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ThrowingPath);
        if (correlationId is not null)
        {
            request.Headers.Add(LogScope.CorrelationHeader, correlationId);
        }

        return await browser.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<ErrorLog> StoredErrorAsync(AppHost app, string correlationId, CancellationToken cancellationToken)
    {
        await app.Services.GetRequiredService<ErrorLogQueue>().FlushAsync(cancellationToken);
        await using var context = this.database.CreateSystemContext();
        var rows = await context.ErrorLogs.AsNoTracking().Where(log => log.CorrelationId == correlationId).ToListAsync(cancellationToken);
        return Assert.Single(rows, log => log.ExceptionType == typeof(ThrowingRoute.RouteException).FullName);
    }

    private sealed class ThrowingRoute : IStartupFilter
    {
        public const string Message = "The test route failed on purpose.";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map(ThrowingPath, route => route.Run(_ => throw new RouteException(Message)));
        };

        public sealed class RouteException : Exception
        {
            public RouteException(string message) : base(message)
            {
            }
        }
    }
}
