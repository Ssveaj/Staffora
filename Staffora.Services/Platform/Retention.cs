using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Platform;

public sealed class RetentionOptions
{
    public const string Section = "Retention";

    public const int MaxBatchSize = 4000;

    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    public bool Enabled { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan FirstPassDelay { get; set; } = TimeSpan.FromMinutes(1);

    public int ErrorLogDays { get; set; } = 90;

    public int ReadNoticeDays { get; set; } = 180;

    public int ProcessedOutboxDays { get; set; } = 30;

    public int PendingDeletionGraceDays { get; set; } = 30;

    public int BatchSize { get; set; } = 1000;
}

public sealed record RetentionPass(DateTimeOffset Started, DateTimeOffset Finished, int ErrorLogEntries, int ReadNotices, int DeliveredMessages, int PurgedTenants, int PurgedRows, int Failures);

public sealed partial class Retention
{
    public const string ErrorLogRule = "error-log";

    public const string ReadNoticesRule = "read-notices";

    public const string ProcessedOutboxRule = "processed-outbox";

    public const string TenantPurgeRule = "tenant-purge";

    private readonly IServiceScopeFactory scopes;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IOptions<RetentionOptions> options;
    private readonly TimeProvider clock;
    private readonly StafforaTelemetry telemetry;
    private readonly ILogger<Retention> log;
    private RetentionPass? lastPass;

    public Retention(IServiceScopeFactory scopes, IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IOptions<RetentionOptions> options, TimeProvider clock, StafforaTelemetry telemetry, ILogger<Retention> log)
    {
        this.scopes = scopes;
        this.dbFactory = dbFactory;
        this.options = options;
        this.clock = clock;
        this.telemetry = telemetry;
        this.log = log;
    }

    public RetentionPass? LastPass => Volatile.Read(ref this.lastPass);

    public async Task<RetentionPass> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var activity = StafforaTelemetry.Source.StartActivity(StafforaTelemetry.RetentionPassActivity);
        var settings = this.options.Value;
        var started = this.clock.GetUtcNow();
        var now = started.UtcDateTime;
        var failures = 0;

        var errorLog = await AttemptAsync(ErrorLogRule, tenantId: null, () => RemoveErrorLogAsync(now.AddDays(-settings.ErrorLogDays), settings, cancellationToken), cancellationToken);
        failures += errorLog is null ? 1 : 0;

        var (notices, messages) = (0, 0);
        foreach (var tenantId in await TenantIdsAsync(cancellationToken))
        {
            var removedNotices = await AttemptAsync(ReadNoticesRule, tenantId, () => RemoveReadNoticesAsync(tenantId, now.AddDays(-settings.ReadNoticeDays), settings, cancellationToken), cancellationToken);
            var removedMessages = await AttemptAsync(ProcessedOutboxRule, tenantId, () => RemoveProcessedMessagesAsync(tenantId, now.AddDays(-settings.ProcessedOutboxDays), settings, cancellationToken), cancellationToken);
            notices += removedNotices ?? 0;
            messages += removedMessages ?? 0;
            failures += (removedNotices is null ? 1 : 0) + (removedMessages is null ? 1 : 0);
        }

        var (tenants, rows) = (0, 0);
        foreach (var tenantId in await TenantsPastGraceAsync(now.AddDays(-settings.PendingDeletionGraceDays), cancellationToken))
        {
            var purged = await AttemptAsync(TenantPurgeRule, tenantId, () => PurgeTenantAsync(tenantId, now.AddDays(-settings.PendingDeletionGraceDays), settings, cancellationToken), cancellationToken);
            tenants += purged is null ? 0 : 1;
            rows += purged ?? 0;
            failures += purged is null ? 1 : 0;
        }

        this.telemetry.RecordRetentionRemoved(ErrorLogRule, errorLog ?? 0);
        this.telemetry.RecordRetentionRemoved(ReadNoticesRule, notices);
        this.telemetry.RecordRetentionRemoved(ProcessedOutboxRule, messages);
        this.telemetry.RecordRetentionRemoved(TenantPurgeRule, rows);

        var pass = new RetentionPass(started, this.clock.GetUtcNow(), errorLog ?? 0, notices, messages, tenants, rows, failures);
        Volatile.Write(ref this.lastPass, pass);
        this.log.LogInformation(
            "The retention pass removed {ErrorLogEntries} error-log rows, {ReadNotices} read notices and {DeliveredMessages} delivered integration messages, and purged {PurgedTenants} tenants ({PurgedRows} rows), with {Failures} failures.",
            pass.ErrorLogEntries, pass.ReadNotices, pass.DeliveredMessages, pass.PurgedTenants, pass.PurgedRows, pass.Failures);
        return pass;
    }

    private async Task<int?> AttemptAsync(string rule, Guid? tenantId, Func<Task<int>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "The retention pass could not apply its {Rule} rule (tenant {TenantId}); it goes on, and the next pass tries again.", rule, tenantId);
            return null;
        }
    }

    private async Task<int> RemoveErrorLogAsync(DateTime before, RetentionOptions settings, CancellationToken cancellationToken)
    {
        var removed = 0;
        while (true)
        {
            int batch;
            await using (var context = this.dbFactory.CreateSystemDbContext())
            {
                using var lease = context.BeginSystemScope("Retention: remove error-log rows older than the error log's period");
                batch = await context.ErrorLogs.Where(row => row.Occurred < before).OrderBy(row => row.Id).Take(settings.BatchSize).ExecuteDeleteAsync(cancellationToken);
            }

            removed += batch;
            if (batch < settings.BatchSize)
            {
                if (removed > 0)
                {
                    this.log.LogInformation("Retention removed {Count} error-log rows older than {Days} days.", removed, settings.ErrorLogDays);
                }

                return removed;
            }
        }
    }

    internal async Task<int> RemoveReadNoticesAsync(Guid tenantId, DateTime before, RetentionOptions settings, CancellationToken cancellationToken)
    {
        const string reason = "Retention: remove one tenant's read notices older than the notices' period";
        var copies = await RemoveInBatchesAsync(reason, settings.BatchSize, context => context.MessageRecipients.Where(copy => copy.TenantId == tenantId && copy.IsRead && copy.ReadAt < before), cancellationToken);
        var delivered = await RemoveInBatchesAsync(reason, settings.BatchSize, context => context.StoredMessages.Where(notice => notice.TenantId == tenantId && notice.Created < before && !context.MessageRecipients.Any(copy => copy.StoredMessageId == notice.Id)), cancellationToken);
        var queued = await RemoveInBatchesAsync(reason, settings.BatchSize, context => context.OutgoingMessages.Where(notice => notice.TenantId == tenantId && notice.Status == OutgoingMessageStatus.Delivered && notice.Created < before && !context.StoredMessages.Any(stored => stored.OutgoingMessageId == notice.Id)), cancellationToken);
        if (copies + delivered + queued > 0)
        {
            this.log.LogInformation(
                "Retention removed {Copies} read notice copies, {Delivered} delivered notices and {Queued} queued notices older than {Days} days in tenant {TenantId}.",
                copies, delivered, queued, settings.ReadNoticeDays, tenantId);
        }

        return copies;
    }

    internal async Task<int> RemoveProcessedMessagesAsync(Guid tenantId, DateTime before, RetentionOptions settings, CancellationToken cancellationToken)
    {
        const string reason = "Retention: remove one tenant's delivered integration messages and receipts older than the outbox's period";
        var messages = await RemoveInBatchesAsync(reason, settings.BatchSize, context => context.IntegrationMessages.Where(message => message.TenantId == tenantId && message.State == IntegrationMessageState.Delivered && message.Finished < before), cancellationToken);
        var receipts = await RemoveInBatchesAsync(reason, settings.BatchSize, context => context.IntegrationReceipts.Where(receipt => receipt.TenantId == tenantId && receipt.Created < before && !context.IntegrationMessages.Any(message => message.Id == receipt.MessageId && message.State != IntegrationMessageState.Delivered)), cancellationToken);
        if (messages + receipts > 0)
        {
            this.log.LogInformation(
                "Retention removed {Messages} delivered integration messages and {Receipts} receipts older than {Days} days in tenant {TenantId}.",
                messages, receipts, settings.ProcessedOutboxDays, tenantId);
        }

        return messages;
    }

    private async Task<int> RemoveInBatchesAsync<T>(string reason, int batchSize, Func<ApplicationDbContext, IQueryable<T>> rows, CancellationToken cancellationToken)
        where T : TenantEntityBase
    {
        var removed = 0;
        while (true)
        {
            await using var context = this.dbFactory.CreateSystemDbContext();
            using var lease = context.BeginSystemScope(reason);
            var batch = await rows(context).AsTracking().OrderBy(row => row.Id).Take(batchSize).ToListAsync(cancellationToken);
            if (batch.Count > 0)
            {
                context.RemoveRange(batch);
                await context.SaveChangesAsync(cancellationToken);
                removed += batch.Count;
            }

            if (batch.Count < batchSize)
            {
                return removed;
            }
        }
    }

    private async Task<List<Guid>> TenantIdsAsync(CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("Retention: list every tenant's id, whose notices and messages are then removed one tenant at a time");
        return await context.Tenants.AsNoTracking().OrderBy(tenant => tenant.Id).Select(tenant => tenant.Id).ToListAsync(cancellationToken);
    }
}

public sealed class RetentionService : BackgroundService
{
    private readonly Retention retention;
    private readonly IOptions<RetentionOptions> options;
    private readonly TimeProvider clock;
    private readonly ILogger<RetentionService> log;

    public RetentionService(Retention retention, IOptions<RetentionOptions> options, TimeProvider clock, ILogger<RetentionService> log)
    {
        this.retention = retention;
        this.options = options;
        this.clock = clock;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = this.options.Value;
        if (!settings.Enabled)
        {
            this.log.LogInformation("The retention pass is off ({Section}:{Setting}); nothing is removed by age and no tenant is purged.", RetentionOptions.Section, nameof(RetentionOptions.Enabled));
            return;
        }

        this.log.LogInformation("The retention pass runs every {Interval}, first after {FirstPassDelay}.", settings.Interval, settings.FirstPassDelay);
        var wait = settings.FirstPassDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, this.clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await this.retention.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, "A retention pass failed; the next one tries again.");
            }

            wait = settings.Interval;
        }
    }
}
