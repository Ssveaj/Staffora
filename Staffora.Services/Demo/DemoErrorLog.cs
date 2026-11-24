using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Services.Demo;

public static class DemoErrorLog
{
    private const int Seed = 4_207;

    public static async Task SeedAsync(IServiceProvider services, TimeProvider clock, Guid platformOwnerId, CancellationToken cancellationToken)
    {
        var contexts = services.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>();
        var now = clock.GetUtcNow().UtcDateTime;
        await using var context = contexts.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("the demo's error-log rows for the platform's error log page");

        var lindale = LindaleDemo.Business;
        var clearshine = ClearshineDemo.Business;
        var tenantNames = new[] { lindale.TenantName, clearshine.TenantName, lindale.Portal.Customer, clearshine.Portal.Customer };
        var tenants = await context.Tenants.AsNoTracking()
            .Where(tenant => tenantNames.Contains(tenant.Name))
            .ToDictionaryAsync(tenant => tenant.Name, tenant => tenant.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var emails = new[] { lindale.SeniorCoordinator.Email, lindale.JuniorCoordinators[0].Email, lindale.JuniorCoordinators[1].Email, clearshine.SeniorCoordinator.Email, clearshine.Owners[0].Email };
        var users = await context.Users.AsNoTracking()
            .Where(user => user.Email != null && emails.Contains(user.Email))
            .ToDictionaryAsync(user => user.Email!, user => user.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        Guid? TenantOf(string name) => tenants.TryGetValue(name, out var id) ? id : null;
        Guid? UserOf(DemoPerson person) => users.TryGetValue(person.Email, out var id) ? id : null;

        var random = new Random(Seed);
        var rows = new List<ErrorLog>();

        DateTime At(int daysAgo, int hour, int? minute = null)
        {
            var day = now.Date.AddDays(-daysAgo);
            var time = day.AddHours(hour).AddMinutes(minute ?? random.Next(0, 60)).AddSeconds(random.Next(0, 60));
            return time < now ? time : now.AddMinutes(-random.Next(5, 50));
        }

        string Correlation() =>
            $"0HN{string.Concat(Enumerable.Range(0, 10).Select(_ => "0123456789ABCDEFGHJKMNPQRSTVWXYZ"[random.Next(32)]))}:{random.Next(1, 0x40):X8}";

        void Add(Issue issue, DateTime occurred, Guid? tenant, Guid? user = null, Guid? impersonator = null) => rows.Add(new ErrorLog
        {
            Occurred = occurred,
            Level = issue.Level,
            Category = issue.Category,
            EventId = issue.EventId,
            EventName = issue.EventName,
            Message = issue.Message,
            ExceptionType = issue.ExceptionType,
            ExceptionDetail = issue.ExceptionType is null ? null : issue.Trace(),
            CorrelationId = issue.FromRequest || issue.ExceptionType is not null ? Correlation() : null,
            RequestPath = issue.Path,
            TenantId = tenant,
            UserId = user,
            ImpersonatorId = impersonator,
        });

        foreach (var (daysAgo, hour, count) in new[] { (0, 13, 2), (2, 15, 6), (4, 9, 3), (6, 14, 4), (18, 10, 2) })
        {
            var start = At(daysAgo, hour, random.Next(0, 10));
            for (var index = 0; index < count; index++)
            {
                Add(Issues.ConcurrencyOnJobs, start.AddMinutes(index * random.Next(4, 11)), TenantOf(lindale.TenantName), UserOf(lindale.SeniorCoordinator));
            }
        }

        foreach (var daysAgo in new[] { 0, 1, 1, 3, 5, 9, 12, 16, 23 })
        {
            var company = random.Next(2) == 0 ? clearshine : lindale;
            Add(Issues.NoticeTimeout, At(daysAgo, random.Next(7, 19)), TenantOf(company.TenantName));
        }

        Add(Issues.CircuitOnCoordination, At(2, 16), TenantOf(lindale.TenantName), UserOf(lindale.JuniorCoordinators[0]));
        Add(Issues.CircuitOnCoordination, At(5, 11), TenantOf(lindale.TenantName), UserOf(lindale.JuniorCoordinators[1]), platformOwnerId);
        Add(Issues.CircuitOnCoordination, At(8, 8), TenantOf(lindale.TenantName), UserOf(lindale.JuniorCoordinators[0]));
        Add(Issues.CircuitOnCoordination, At(27, 14), TenantOf(clearshine.TenantName), UserOf(clearshine.SeniorCoordinator));

        foreach (var daysAgo in new[] { 0, 1, 3, 4, 10 })
        {
            Add(Issues.JobClockRetry, At(daysAgo, 6, random.Next(0, 3)), TenantOf(clearshine.TenantName));
        }

        Add(Issues.OutboxGivenUp, At(0, Math.Max(0, now.Hour - 1)), TenantOf(lindale.Portal.Customer));
        Add(Issues.OutboxGivenUp, At(3, 22), TenantOf(lindale.Portal.Customer));

        Add(Issues.OverviewQueryTimeout, At(1, 8), tenant: null);
        Add(Issues.OverviewQueryTimeout, At(14, 9), tenant: null);
        Add(Issues.DatabaseUnreachable, At(20, 3), TenantOf(clearshine.TenantName), UserOf(clearshine.Owners[0]));

        Add(Issues.QueueOverflow, At(2, 15, 40), tenant: null);

        context.ErrorLogs.AddRange(rows);
        await context.SaveChangesAsync(cancellationToken);
    }

    private sealed record Issue(ErrorLogLevel Level, string Category, string Message, string? ExceptionType, string? ExceptionMessage, string[] Frames, string? Path = null, int EventId = 0, string? EventName = null, bool FromRequest = false)
    {
        public string Trace()
        {
            var text = new StringBuilder($"{this.ExceptionType}: {this.ExceptionMessage}");
            foreach (var frame in this.Frames)
            {
                text.Append(Environment.NewLine).Append("   at ").Append(frame);
            }

            return text.ToString();
        }
    }

    private static class Issues
    {
        public static readonly Issue ConcurrencyOnJobs = new(
            ErrorLogLevel.Error,
            "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware",
            "An unhandled exception has occurred while executing the request.",
            "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException",
            "The database operation was expected to affect 1 row(s), but actually affected 0 row(s); data may have been modified or deleted since entities were loaded.",
            [
                "Microsoft.EntityFrameworkCore.Update.AffectedCountModificationCommandBatch.ThrowAggregateUpdateConcurrencyExceptionAsync(RelationalDataReader reader, Int32 commandIndex, Int32 expectedRowsAffected, Int32 rowsAffected, CancellationToken cancellationToken)",
                "Staffora.Data.ApplicationDbContext.SaveChangesAsync(Boolean acceptAllChangesOnSuccess, CancellationToken cancellationToken)",
                "Staffora.Services.Workflow.WorkflowService.SaveAndTransitionAsync[TState,TEntity](DetachedEntityUpdater`1 updater, String transitionKey, String comment, CancellationToken cancellationToken)",
                "Staffora.Services.Jobs.JobService.UpdateJobAndTransitionAsync(DetachedEntityUpdater`1 updater, String transitionKey, String comment, CancellationToken cancellationToken)",
                "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddlewareImpl.Invoke(HttpContext context)",
            ],
            Path: "/tenant/jobs",
            EventId: 1,
            EventName: "UnhandledException",
            FromRequest: true);

        public static readonly Issue NoticeTimeout = new(
            ErrorLogLevel.Error,
            "Staffora.Services.Messaging.OutgoingMessageDispatcher",
            "Sending the notice to the in-app channel failed; it will be tried again.",
            "System.TimeoutException",
            "The operation has timed out after 00:00:30.",
            [
                "Staffora.Services.Messaging.InAppChannelSender.SendAsync(OutgoingMessage message, CancellationToken cancellationToken)",
                "Staffora.Services.Messaging.OutgoingMessageDispatcher.DispatchAsync(Guid messageId, CancellationToken cancellationToken)",
            ]);

        public static readonly Issue CircuitOnCoordination = new(
            ErrorLogLevel.Error,
            "Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost",
            "Unhandled exception in circuit while rendering the coordination workspace.",
            "System.InvalidOperationException",
            "Sequence contains no matching element.",
            [
                "System.Linq.ThrowHelper.ThrowNoMatchException()",
                "Staffora.Blazor.Components.Coordination.CoordinationTimeline.BuildRenderTree(RenderTreeBuilder builder)",
                "Microsoft.AspNetCore.Components.Rendering.ComponentState.RenderIntoBatch(RenderBatchBuilder batchBuilder, RenderFragment renderFragment, Exception& renderFragmentException)",
            ],
            Path: "/coordination",
            EventId: 111,
            EventName: "CircuitUnhandledException",
            FromRequest: true);

        public static readonly Issue JobClockRetry = new(
            ErrorLogLevel.Warning,
            "Staffora.Services.Jobs.JobClock",
            "The job clock could not move a job at its pass; it will try again at the next one.",
            null,
            null,
            []);

        public static readonly Issue OutboxGivenUp = new(
            ErrorLogLevel.Critical,
            "Staffora.Services.Integration.IntegrationOutboxDispatcher",
            "The integration message could not be delivered and was given up.",
            "System.Net.Http.HttpRequestException",
            "Response status code does not indicate success: 503 (Service Unavailable).",
            [
                "System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()",
                "Staffora.Services.Integration.IntegrationOutboxDispatcher.DeliverAsync(IntegrationMessage message, CancellationToken cancellationToken)",
                "Staffora.Services.Integration.IntegrationOutboxDispatcher.RunOnceAsync(CancellationToken cancellationToken)",
                "Microsoft.Extensions.Hosting.Internal.BackgroundService.ExecuteAsync(CancellationToken stoppingToken)",
            ]);

        public static readonly Issue OverviewQueryTimeout = new(
            ErrorLogLevel.Error,
            "Microsoft.EntityFrameworkCore.Query",
            "An exception occurred while iterating over the results of a query for the platform overview.",
            "Microsoft.Data.SqlClient.SqlException",
            "Execution Timeout Expired. The timeout period elapsed prior to completion of the operation or the server is not responding.",
            [
                "Microsoft.Data.SqlClient.SqlCommand.EndExecuteReaderAsync(IAsyncResult asyncResult)",
                "Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.AsyncEnumerator.MoveNextAsync()",
                "Staffora.Services.Platform.PlatformConsoleService.ReadOverviewAsync(CancellationToken cancellationToken)",
            ],
            Path: "/superadmin/overview",
            EventId: 10100,
            EventName: "QueryIterationFailed",
            FromRequest: true);

        public static readonly Issue DatabaseUnreachable = new(
            ErrorLogLevel.Critical,
            "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware",
            "An unhandled exception has occurred while executing the request.",
            "Microsoft.Data.SqlClient.SqlException",
            "A network-related or instance-specific error occurred while establishing a connection to SQL Server.",
            [
                "Microsoft.Data.SqlClient.SqlInternalConnectionTds..ctor(DbConnectionPoolIdentity identity, SqlConnectionString connectionOptions)",
                "Microsoft.EntityFrameworkCore.Storage.RelationalConnection.OpenInternalAsync(Boolean errorsExpected, CancellationToken cancellationToken)",
                "Staffora.Services.Authorization.UserSessionService.InitializeFromClaimsAsync()",
            ],
            Path: "/tenant/management/overview",
            EventId: 1,
            EventName: "UnhandledException",
            FromRequest: true);

        public static readonly Issue QueueOverflow = new(
            ErrorLogLevel.Warning,
            "Staffora.Services.Diagnostics.ErrorLogWriter",
            "The error log's queue was full; 4 entries were dropped.",
            null,
            null,
            []);
    }
}
