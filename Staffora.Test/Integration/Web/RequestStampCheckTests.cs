using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Diagnostics;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class RequestStampCheckTests : IClassFixture<RealDatabase>
{
    private const string Password = "Correct-Horse-7";
    private const string AuthCookie = "Staffora.Auth";

    private readonly RealDatabase database;

    public RequestStampCheckTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_refused_cookie_is_deleted_by_the_response_that_refuses_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, member) = await MemberAsync(cancellationToken);
        await using var app = AppHost.Start(this.database);
        var stale = await CookieHeaderAsync(app, member);
        await NewPasswordAsync(scenario, member);

        var refused = await SendAsync(app, stale, HttpMethod.Get, "/test/claims");
        Assert.Equal((HttpStatusCode.Unauthorized, true), (refused.Status, refused.Deleted));
    }

    [Fact]
    public async Task A_checked_request_reads_the_cookies_user_and_tenant_in_one_query()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, member) = await MemberAsync(cancellationToken);
        var probe = new CommandProbe();
        await using var app = AppHost.Start(this.database, probe.Register(this.database));
        var cookie = await CookieHeaderAsync(app, member);

        probe.Counting = true;
        var admitted = await SendAsync(app, cookie, HttpMethod.Get, "/test/claims");
        probe.Counting = false;

        Assert.Equal(HttpStatusCode.OK, admitted.Status);
        var command = Assert.Single(probe.Commands);
        Assert.Contains("AspNetUsers", command, StringComparison.Ordinal);
        Assert.Contains("Tenants", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_static_asset_is_served_to_a_stale_cookie_without_a_query_while_every_page_and_endpoint_refuses_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, member) = await MemberAsync(cancellationToken);
        var probe = new CommandProbe();
        await using var app = AppHost.Start(this.database, probe.Register(this.database));
        var stale = await CookieHeaderAsync(app, member);
        await NewPasswordAsync(scenario, member);

        probe.Counting = true;
        var css = await SendAsync(app, stale, HttpMethod.Get, "/app.css");
        var icon = await SendAsync(app, stale, HttpMethod.Get, "/favicon.png");
        probe.Counting = false;
        Assert.Equal((HttpStatusCode.OK, false), (css.Status, css.Deleted));
        Assert.Equal((HttpStatusCode.OK, false), (icon.Status, icon.Deleted));
        Assert.DoesNotContain(probe.Commands, command => command.Contains("SecurityStamp", StringComparison.Ordinal));

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/"), (HttpMethod.Get, "/me/profile"), (HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1"), (HttpMethod.Get, "/no-such-page-" + Guid.NewGuid().ToString("N")), (HttpMethod.Get, "/test/claims") })
        {
            var answer = await SendAsync(app, stale, method, path);
            Assert.True(answer.Deleted, $"{method} {path}: {(int)answer.Status}, the cookie not deleted");
        }

        Assert.Equal(HttpStatusCode.Redirect, (await SendAsync(app, stale, HttpMethod.Get, "/me/profile")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(app, stale, HttpMethod.Get, "/test/claims")).Status);
    }

    [Fact]
    public async Task A_database_that_does_not_answer_refuses_the_cookie_with_the_status_page_logs_the_error_and_keeps_the_cookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, member) = await MemberAsync(cancellationToken);
        var probe = new CommandProbe();
        await using var app = AppHost.Start(this.database, probe.Register(this.database));
        var cookie = await CookieHeaderAsync(app, member);
        var correlationId = Guid.NewGuid().ToString("N");

        probe.FailUsers = true;
        HttpStatusCode status;
        bool deleted;
        string body;
        using (var browser = app.CreateBrowser())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add(LogScope.CorrelationHeader, correlationId);
            using var response = await browser.Http.SendAsync(request, cancellationToken);
            (status, deleted, body) = (response.StatusCode, Deletes(response), await response.Content.ReadAsStringAsync(cancellationToken));
        }

        probe.FailUsers = false;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("Something went wrong", body, StringComparison.Ordinal);
        Assert.False(deleted);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(app, cookie, HttpMethod.Get, "/test/claims")).Status);

        await app.Services.GetRequiredService<ErrorLogQueue>().FlushAsync(cancellationToken);
        await using var context = this.database.CreateSystemContext();
        var logged = await context.ErrorLogs.AsNoTracking().Where(log => log.CorrelationId == correlationId).ToListAsync(cancellationToken);
        Assert.Contains(logged, log => log.Category == RequestStampCheck.LogCategory && log.ExceptionType == typeof(DatabaseOutageException).FullName);
    }

    private async Task<(TwoTenantScenario Scenario, Guid Member)> MemberAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var member = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Member", cancellationToken);
        await using var setup = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var users = setup.Get<UserManager<ApplicationUser>>();
        Assert.True((await users.AddPasswordAsync((await users.FindByIdAsync(member.ToString()))!, Password)).Succeeded);
        return (scenario, member);
    }

    private async Task NewPasswordAsync(TwoTenantScenario scenario, Guid member)
    {
        await using var admin = ServiceHost.Create(this.database, scenario.A.Runtime());
        var stepped = await admin.Get<UserService>().StepUserAsync(member, ApplicationUserWorkflowAttribute.NewPasswordStep);
        Assert.True(stepped.IsSuccess, stepped.Error);
    }

    private static async Task<string> CookieHeaderAsync(AppHost app, Guid userId)
    {
        using var browser = app.CreateBrowser();
        using var response = await browser.Http.PostAsync($"/test/sign-in/{userId}", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AuthCookieOf(response)!;
    }

    private static string? AuthCookieOf(HttpResponseMessage response)
    {
        var set = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(cookie => cookie.Split(';')[0]).Where(cookie => cookie.StartsWith(AuthCookie, StringComparison.Ordinal) && !cookie.EndsWith('=')).ToList()
            : [];
        return set.Count == 0 ? null : string.Join("; ", set);
    }

    private static async Task<(HttpStatusCode Status, bool Deleted)> SendAsync(AppHost app, string cookie, HttpMethod method, string path)
    {
        using var browser = app.CreateBrowser();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        using var response = await browser.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, Deletes(response));
    }

    private static bool Deletes(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(cookie => cookie.StartsWith(AuthCookie + "=;", StringComparison.Ordinal) || (cookie.StartsWith(AuthCookie, StringComparison.Ordinal) && cookie.Contains("1970", StringComparison.Ordinal)));

    private sealed class CommandProbe : DbCommandInterceptor
    {
        private volatile bool counting;
        private volatile bool failUsers;

        public bool Counting { get => this.counting; set => this.counting = value; }

        public bool FailUsers { get => this.failUsers; set => this.failUsers = value; }

        public ConcurrentQueue<string> Commands { get; } = new();

        public Action<IServiceCollection> Register(RealDatabase database) => services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.AddSingleton(new DbContextOptionsBuilder<ApplicationDbContext>(ApplicationDbContextOptions.Create(database.ConnectionString, throwOnUnorderedRowLimit: true)).AddInterceptors(this).Options);
        };

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) => See(command, result);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(See(command, result));

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) => See(command, result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(See(command, result));

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) => See(command, result);

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(See(command, result));

        private T See<T>(DbCommand command, T result)
        {
            if (FailUsers && command.CommandText.Contains("AspNetUsers", StringComparison.Ordinal))
            {
                throw new DatabaseOutageException();
            }

            if (Counting && !command.CommandText.Contains("ErrorLogs", StringComparison.Ordinal))
            {
                Commands.Enqueue(command.CommandText);
            }

            return result;
        }
    }

    private sealed class DatabaseOutageException : Exception
    {
        public DatabaseOutageException() : base("The database did not answer (test).")
        {
        }
    }
}
