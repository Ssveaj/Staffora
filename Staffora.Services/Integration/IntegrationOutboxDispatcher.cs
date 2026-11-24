using System.Data;
using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Messaging;

namespace Staffora.Services.Integration;

public sealed class IntegrationOutboxOptions
{
    public const string Section = "IntegrationOutbox";

    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    public const int MaxBatchSize = 1000;

    public bool Enabled { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    public int BatchSize { get; set; } = 100;
}

public sealed record IntegrationOutboxPass(int Delivered, int Failed, int DeadLettered, int Held, int NoticesDelivered = 0, int NoticesFailed = 0, int NoticesHeld = 0);

public sealed class IntegrationOutboxDispatcher
{
    public const string ListReason = "Integration outbox: list the messages and notices due in every tenant";

    public const string ClaimReason = "Integration outbox: claim one message of its sending tenant";

    public const string DeliverReason = "Integration outbox: read and mark the sending tenant's message it delivers in its target tenant";

    public const string RecordReason = "Integration outbox: record a delivery's outcome on the sending tenant's message";

    public const string NoticeReason = "Integration outbox: claim or record one notice it delivers again in its tenant";

    public const string BacklogReason = "Integration outbox: count the pending and dead-lettered messages of every tenant for telemetry";

    public const string NotLinkedReason = "The two tenants are not linked.";

    public const string SpentReason = "Its attempts are spent.";

    private const string Actor = "integration outbox";

    private readonly IServiceScopeFactory scopes;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IntegrationHandlerRegistry handlers;
    private readonly IOptions<IntegrationOutboxOptions> options;
    private readonly TimeProvider clock;
    private readonly ILogger<IntegrationOutboxDispatcher> log;
    private readonly StafforaTelemetry telemetry;

    public IntegrationOutboxDispatcher(IServiceScopeFactory scopes, IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IntegrationHandlerRegistry handlers, IOptions<IntegrationOutboxOptions> options, TimeProvider clock, ILogger<IntegrationOutboxDispatcher> log, StafforaTelemetry telemetry)
    {
        this.scopes = scopes;
        this.dbFactory = dbFactory;
        this.handlers = handlers;
        this.options = options;
        this.clock = clock;
        this.log = log;
        this.telemetry = telemetry;
    }

    public async Task<IntegrationOutboxPass> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var batch = Math.Clamp(this.options.Value.BatchSize, 1, IntegrationOutboxOptions.MaxBatchSize);
        var taken = new HashSet<Guid>();
        var (delivered, failed, deadLettered, held, noticesDelivered, noticesFailed, noticesHeld) = (0, 0, 0, 0, 0, 0, 0);
        while (taken.Count < batch)
        {
            var (messages, notices) = await ListDueAsync(taken, batch - taken.Count, cancellationToken);
            if (messages.Count == 0 && notices.Count == 0)
            {
                break;
            }

            foreach (var (id, kind) in messages.TakeWhile(_ => taken.Count < batch))
            {
                cancellationToken.ThrowIfCancellationRequested();
                taken.Add(id);
                switch (await MeasuredAsync(id, kind, cancellationToken))
                {
                    case Outcome.Delivered:
                        delivered++;
                        break;
                    case Outcome.Failed:
                        failed++;
                        break;
                    case Outcome.DeadLettered:
                        deadLettered++;
                        break;
                    case Outcome.Held:
                        held++;
                        break;
                }
            }

            foreach (var id in notices.TakeWhile(_ => taken.Count < batch))
            {
                cancellationToken.ThrowIfCancellationRequested();
                taken.Add(id);
                switch (await GuardedAsync(id, ProcessNoticeAsync, cancellationToken))
                {
                    case Outcome.Delivered:
                        noticesDelivered++;
                        break;
                    case Outcome.Failed or Outcome.DeadLettered:
                        noticesFailed++;
                        break;
                    case Outcome.Held:
                        noticesHeld++;
                        break;
                }
            }
        }

        await RecordBacklogAsync(cancellationToken);
        return new IntegrationOutboxPass(delivered, failed, deadLettered, held, noticesDelivered, noticesFailed, noticesHeld);
    }

    private async Task<Outcome> MeasuredAsync(Guid id, string kind, CancellationToken cancellationToken)
    {
        var startedAt = this.telemetry.Timestamp();
        using var activity = StafforaTelemetry.Source.StartActivity(StafforaTelemetry.OutboxDispatchActivity);
        activity?.SetTag(StafforaTelemetry.MessageIdTag, id.ToString());
        activity?.SetTag(StafforaTelemetry.MessageKindTag, kind);
        var outcome = Outcome.Skipped;
        try
        {
            outcome = await GuardedAsync(id, ProcessMessageAsync, cancellationToken);
            return outcome;
        }
        finally
        {
            var name = OutcomeName(outcome);
            activity?.SetTag(StafforaTelemetry.OutcomeTag, name);
            this.telemetry.RecordOutboxDispatch(kind, name, startedAt);
        }
    }

    private static string OutcomeName(Outcome outcome) => outcome switch
    {
        Outcome.Delivered => "delivered",
        Outcome.Failed => "failed",
        Outcome.DeadLettered => "dead_lettered",
        Outcome.Held => "held",
        _ => "skipped",
    };

    private async Task RecordBacklogAsync(CancellationToken cancellationToken)
    {
        if (!this.telemetry.ReadsOutboxBacklog)
        {
            return;
        }

        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope(BacklogReason);
        var counts = await context.IntegrationMessages.AsNoTracking()
            .Where(message => message.State == IntegrationMessageState.Pending || message.State == IntegrationMessageState.DeadLettered)
            .GroupBy(message => message.State)
            .Select(group => new { State = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken);
        this.telemetry.RecordOutboxBacklog(
            counts.Where(count => count.State == IntegrationMessageState.Pending).Sum(count => count.Count),
            counts.Where(count => count.State == IntegrationMessageState.DeadLettered).Sum(count => count.Count));
    }

    private enum Outcome
    {
        Skipped = 0,
        Delivered = 1,
        Failed = 2,
        DeadLettered = 3,
        Held = 4,
    }

    private sealed record MessageClaim(Guid Id, Guid SourceTenantId, Guid TargetTenantId, string Kind, Guid CorrelationId, string Payload, Guid Token, int Attempts);

    private sealed record NoticeClaim(Guid Id, Guid TenantId, int Attempts, OutgoingMessageStatus PriorStatus, DateTime? PriorNextAttempt);

    private DateTime Now() => this.clock.GetUtcNow().UtcDateTime;

    private async Task<Outcome> GuardedAsync(Guid id, Func<Guid, CancellationToken, Task<Outcome>> process, CancellationToken cancellationToken)
    {
        try
        {
            return await process(id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "The integration outbox's dispatcher could not take {Id}; it goes on with the next, and a later pass takes it again.", id);
            return Outcome.Skipped;
        }
    }

    private static Expression<Func<IntegrationMessage, bool>> DueMessage(DateTime now) => message =>
        message.State == IntegrationMessageState.Pending && message.NextAttempt <= now && (message.ClaimedUntil == null || message.ClaimedUntil <= now);

    private static Expression<Func<OutgoingMessage, bool>> DueNotice(DateTime now, DateTime graceCut) => notice =>
        (notice.Status == OutgoingMessageStatus.Pending && notice.Created <= graceCut && (notice.NextAttempt == null || notice.NextAttempt <= now))
        || (notice.Status == OutgoingMessageStatus.Failed && notice.NextAttempt != null && notice.NextAttempt <= now);

    private async Task<(List<(Guid Id, string Kind)> Messages, List<Guid> Notices)> ListDueAsync(IReadOnlyCollection<Guid> taken, int take, CancellationToken cancellationToken)
    {
        var (now, skip) = (Now(), taken.ToList());
        var graceCut = now - IntegrationRetryRules.NoticeGrace;
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope(ListReason);
        var messages = await context.IntegrationMessages.AsNoTracking()
            .Where(DueMessage(now))
            .Where(message => !skip.Contains(message.Id)
                && context.Tenants.Any(tenant => tenant.Id == message.TenantId && tenant.State == TenantState.Active)
                && context.Tenants.Any(tenant => tenant.Id == message.TargetTenantId && tenant.State == TenantState.Active)
                && !context.IntegrationMessages.Any(earlier => earlier.TenantId == message.TenantId && earlier.CorrelationId == message.CorrelationId
                    && earlier.Sequence < message.Sequence && earlier.State == IntegrationMessageState.Pending))
            .OrderBy(message => message.Sequence)
            .Select(message => new { message.Id, message.Kind })
            .Take(take)
            .ToListAsync(cancellationToken);

        var notices = await context.OutgoingMessages.AsNoTracking()
            .Where(DueNotice(now, graceCut))
            .Where(notice => !skip.Contains(notice.Id)
                && context.Tenants.Any(tenant => tenant.Id == notice.TenantId && (tenant.State == TenantState.Setup || tenant.State == TenantState.Active)))
            .OrderBy(notice => notice.Created)
            .ThenBy(notice => notice.Id)
            .Select(notice => notice.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        return ([.. messages.Select(message => (message.Id, message.Kind))], notices);
    }

    private async Task<Outcome> ProcessMessageAsync(Guid id, CancellationToken cancellationToken)
    {
        var (outcome, claim) = await ClaimAsync(id, cancellationToken);
        if (claim is null)
        {
            return outcome;
        }

        try
        {
            var delivered = await DeliverAsync(claim, cancellationToken);
            if (delivered == Outcome.Held)
            {
                return await ReleaseHeldAsync(claim, cancellationToken);
            }

            return delivered;
        }
        catch (DbUpdateConcurrencyException conflict) when (conflict.Entries.Any(entry => entry.Entity is IntegrationMessage))
        {
            this.log.LogInformation("The integration message {MessageId} was claimed by another dispatcher while this one delivered it; nothing of this delivery was kept.", claim.Id);
            return Outcome.Skipped;
        }
        catch (TenantHeldException)
        {
            return await ReleaseHeldAsync(claim, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return await RecordFailureAsync(claim, exception, cancellationToken);
        }
    }

    private async Task<(Outcome Outcome, MessageClaim? Claim)> ClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope(ClaimReason);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var now = Now();
        var row = await context.IntegrationMessages.AsTracking().Where(DueMessage(now)).FirstOrDefaultAsync(message => message.Id == id, cancellationToken);
        if (row is null)
        {
            return (Outcome.Skipped, null);
        }

        var (sourceId, targetId, correlation, sequence) = (row.TenantId, row.TargetTenantId, row.CorrelationId, row.Sequence);
        var sourceState = await context.HoldTenantAsync(sourceId, cancellationToken);
        var targetState = await context.HoldTenantAsync(targetId, cancellationToken);
        if (!TenantStates.ExchangesMessages(sourceState) || !TenantStates.ExchangesMessages(targetState))
        {
            return (Outcome.Held, null);
        }

        if (await context.IntegrationMessages.AnyAsync(earlier => earlier.TenantId == sourceId && earlier.CorrelationId == correlation && earlier.Sequence < sequence && earlier.State == IntegrationMessageState.Pending, cancellationToken))
        {
            return (Outcome.Skipped, null);
        }

        var parents = await context.Tenants.AsNoTracking()
            .Where(tenant => tenant.Id == sourceId || tenant.Id == targetId)
            .Select(tenant => new { tenant.Id, tenant.ParentTenantId, tenant.IsRootTenant })
            .ToListAsync(cancellationToken);
        var linked = !parents.Any(tenant => tenant.IsRootTenant)
            && TenantStates.AreLinked(sourceId, parents.FirstOrDefault(tenant => tenant.Id == sourceId)?.ParentTenantId, targetId, parents.FirstOrDefault(tenant => tenant.Id == targetId)?.ParentTenantId);
        if (!linked || IntegrationRetryRules.IsSpent(row.Attempts))
        {
            var reason = linked ? SpentReason : NotLinkedReason;
            GiveUp(row, reason, now);
            context.StampOnOtherTenantsBehalf();
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            this.log.LogError("The integration message {MessageId} ({Kind}) of tenant {SourceTenantId} to tenant {TargetTenantId} is a dead letter: {Reason}", row.Id, row.Kind, sourceId, targetId, reason);
            return (Outcome.DeadLettered, null);
        }

        var token = Guid.NewGuid();
        row.Attempts++;
        row.ClaimToken = token;
        row.ClaimedUntil = now + IntegrationRetryRules.ClaimLease;
        context.StampOnOtherTenantsBehalf();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (Outcome.Skipped, null);
        }

        await transaction.CommitAsync(CancellationToken.None);
        return (Outcome.Skipped, new MessageClaim(row.Id, sourceId, targetId, row.Kind, correlation, row.Payload, token, row.Attempts));
    }

    private async Task<Outcome> DeliverAsync(MessageClaim claim, CancellationToken cancellationToken)
    {
        await using var scope = this.scopes.CreateAsyncScope();
        var runtime = SystemAccountSession.Apply(scope.ServiceProvider.GetRequiredService<IRuntimeExecutionContext>(), claim.TargetTenantId, Actor);
        using var logScope = this.log.BeginScope("Integration outbox delivering {MessageId} in tenant {TenantId}", claim.Id, claim.TargetTenantId);
        await using var context = this.dbFactory.CreateDbContext(runtime);

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var targetState = await context.HoldTenantAsync(claim.TargetTenantId, cancellationToken);
        IntegrationMessage? row;
        using (context.BeginSystemScope(DeliverReason))
        {
            var sourceState = await context.HoldTenantAsync(claim.SourceTenantId, cancellationToken);
            if (!TenantStates.ExchangesMessages(targetState) || !TenantStates.ExchangesMessages(sourceState))
            {
                return Outcome.Held;
            }

            var (id, sourceId, token) = (claim.Id, claim.SourceTenantId, claim.Token);
            row = await context.IntegrationMessages.AsTracking()
                .FirstOrDefaultAsync(message => message.Id == id && message.TenantId == sourceId && message.ClaimToken == token && message.State == IntegrationMessageState.Pending, cancellationToken);
        }

        if (row is null)
        {
            this.log.LogInformation("The integration message {MessageId} was claimed by another dispatcher before this one delivered it.", claim.Id);
            return Outcome.Skipped;
        }

        var delivery = new IntegrationDelivery(claim.Id, claim.Kind, claim.SourceTenantId, claim.TargetTenantId, claim.CorrelationId, claim.Payload, scope.ServiceProvider);
        var handling = await this.handlers.HandleOnceAsync(delivery, context, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        using (context.BeginSystemScope(DeliverReason))
        {
            row.State = IntegrationMessageState.Delivered;
            row.Finished = Now();
            row.ClaimedUntil = null;
            row.LastFailure = null;

            context.StampOnOtherTenantsBehalf();
            await context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(CancellationToken.None);
        this.log.LogInformation("Delivered the integration message {MessageId} ({Kind}) of tenant {SourceTenantId} in tenant {TargetTenantId}{Again}.", claim.Id, claim.Kind, claim.SourceTenantId, claim.TargetTenantId, handling == IntegrationHandling.AlreadyHandled ? ", which had handled it before" : string.Empty);

        await delivery.AfterCommit.RunAsync(this.log);
        return Outcome.Delivered;
    }

    private async Task<Outcome> RecordFailureAsync(MessageClaim claim, Exception failure, CancellationToken cancellationToken)
    {
        var outcome = await UpdateClaimedAsync(claim, row =>
        {
            var now = Now();
            row.ClaimedUntil = null;
            row.LastFailure = FailureOf(failure);
            if (IntegrationRetryRules.IsSpent(row.Attempts))
            {
                GiveUp(row, row.LastFailure, now);
                return Outcome.DeadLettered;
            }

            row.NextAttempt = now + IntegrationRetryRules.RetryAfter(row.Attempts);
            return Outcome.Failed;
        }, cancellationToken);
        if (outcome == Outcome.DeadLettered)
        {
            this.log.LogError(failure, "The integration message {MessageId} ({Kind}) of tenant {SourceTenantId} to tenant {TargetTenantId} is a dead letter after {Attempts} attempts.", claim.Id, claim.Kind, claim.SourceTenantId, claim.TargetTenantId, claim.Attempts);
        }
        else if (outcome == Outcome.Failed)
        {
            this.log.LogWarning(failure, "Delivering the integration message {MessageId} ({Kind}) of tenant {SourceTenantId} in tenant {TargetTenantId} failed (attempt {Attempts}); it is tried again after {Wait}.", claim.Id, claim.Kind, claim.SourceTenantId, claim.TargetTenantId, claim.Attempts, IntegrationRetryRules.RetryAfter(claim.Attempts));
        }

        return outcome;
    }

    private async Task<Outcome> ReleaseHeldAsync(MessageClaim claim, CancellationToken cancellationToken)
    {
        var outcome = await UpdateClaimedAsync(claim, row =>
        {
            row.Attempts--;
            row.ClaimedUntil = null;
            return Outcome.Held;
        }, cancellationToken);
        if (outcome == Outcome.Held)
        {
            this.log.LogInformation("Held the integration message {MessageId} ({Kind}) of tenant {SourceTenantId} to tenant {TargetTenantId}: a tenant is not Active.", claim.Id, claim.Kind, claim.SourceTenantId, claim.TargetTenantId);
        }

        return outcome;
    }

    private async Task<Outcome> UpdateClaimedAsync(MessageClaim claim, Func<IntegrationMessage, Outcome> change, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = this.dbFactory.CreateSystemDbContext();
            using var lease = context.BeginSystemScope(RecordReason);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            await context.HoldTenantAsync(claim.SourceTenantId, CancellationToken.None);
            var (id, sourceId, token) = (claim.Id, claim.SourceTenantId, claim.Token);
            var row = await context.IntegrationMessages.AsTracking()
                .FirstOrDefaultAsync(message => message.Id == id && message.TenantId == sourceId && message.ClaimToken == token && message.State == IntegrationMessageState.Pending, CancellationToken.None);
            if (row is null)
            {
                return Outcome.Skipped;
            }

            var outcome = change(row);
            context.StampOnOtherTenantsBehalf();
            await context.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return outcome;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Outcome.Skipped;
        }
    }

    private static void GiveUp(IntegrationMessage row, string reason, DateTime now)
    {
        row.State = IntegrationMessageState.DeadLettered;
        row.Finished = now;
        row.ClaimedUntil = null;
        row.LastFailure = reason;
    }

    private static string FailureOf(Exception failure)
    {
        var name = failure.GetType().Name;
        return name.Length <= IntegrationMessage.MaxFailureLength ? name : name[..IntegrationMessage.MaxFailureLength];
    }

    private async Task<Outcome> ProcessNoticeAsync(Guid id, CancellationToken cancellationToken)
    {
        var (outcome, claim) = await ClaimNoticeAsync(id, cancellationToken);
        if (claim is null)
        {
            return outcome;
        }

        try
        {
            await using var scope = this.scopes.CreateAsyncScope();
            var runtime = SystemAccountSession.Apply(scope.ServiceProvider.GetRequiredService<IRuntimeExecutionContext>(), claim.TenantId, Actor);
            using var logScope = this.log.BeginScope("Integration outbox delivering the notice {MessageId} in tenant {TenantId}", claim.Id, claim.TenantId);
            OutgoingMessage? notice;
            await using (var context = this.dbFactory.CreateDbContext(runtime))
            {
                var (noticeId, tenantId, attempts) = (claim.Id, claim.TenantId, claim.Attempts);
                notice = await context.OutgoingMessages.AsNoTracking()
                    .FirstOrDefaultAsync(row => row.Id == noticeId && row.TenantId == tenantId && row.Attempts == attempts && row.Status == OutgoingMessageStatus.Pending, cancellationToken);
            }

            if (notice is null)
            {
                return Outcome.Skipped;
            }

            foreach (var sender in scope.ServiceProvider.GetRequiredService<MessageChannelRouter>().SendersOf(notice))
            {
                await sender.SendAsync(notice, cancellationToken);
            }

            this.log.LogInformation("Delivered the notice {MessageId} ({MessageType}) of tenant {TenantId} again (attempt {Attempts}).", claim.Id, notice.MessageType, claim.TenantId, claim.Attempts);
            return Outcome.Delivered;
        }
        catch (DbUpdateConcurrencyException)
        {
            this.log.LogInformation("The notice {MessageId} was taken by another dispatcher while this one delivered it.", claim.Id);
            return Outcome.Skipped;
        }
        catch (TenantHeldException)
        {
            return await UpdateClaimedNoticeAsync(claim, row =>
            {
                row.Status = claim.PriorStatus;
                row.NextAttempt = claim.PriorNextAttempt;
                row.Attempts--;
                return Outcome.Held;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var recorded = await UpdateClaimedNoticeAsync(claim, row =>
            {
                row.Status = OutgoingMessageStatus.Failed;
                row.NextAttempt = IntegrationRetryRules.IsSpent(row.Attempts) ? null : Now() + IntegrationRetryRules.RetryAfter(row.Attempts);
                return row.NextAttempt is null ? Outcome.DeadLettered : Outcome.Failed;
            }, cancellationToken);
            if (recorded == Outcome.DeadLettered)
            {
                this.log.LogError(exception, "Delivering the notice {MessageId} of tenant {TenantId} failed {Attempts} times; it is not delivered and tried no more.", claim.Id, claim.TenantId, claim.Attempts);
            }
            else if (recorded == Outcome.Failed)
            {
                this.log.LogWarning(exception, "Delivering the notice {MessageId} of tenant {TenantId} again failed (attempt {Attempts}); it is tried again after {Wait}.", claim.Id, claim.TenantId, claim.Attempts, IntegrationRetryRules.RetryAfter(claim.Attempts));
            }

            return recorded;
        }
    }

    private async Task<(Outcome Outcome, NoticeClaim? Claim)> ClaimNoticeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope(NoticeReason);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var now = Now();
        var row = await context.OutgoingMessages.AsTracking().Where(DueNotice(now, now - IntegrationRetryRules.NoticeGrace)).FirstOrDefaultAsync(notice => notice.Id == id, cancellationToken);
        if (row is null)
        {
            return (Outcome.Skipped, null);
        }

        if (TenantStates.HoldsData(await context.HoldTenantAsync(row.TenantId, cancellationToken)))
        {
            return (Outcome.Held, null);
        }

        var (priorStatus, priorNext) = (row.Status, row.NextAttempt);
        if (IntegrationRetryRules.IsSpent(row.Attempts))
        {
            row.Status = OutgoingMessageStatus.Failed;
            row.NextAttempt = null;
        }
        else
        {
            row.Status = OutgoingMessageStatus.Pending;
            row.Attempts++;
            row.NextAttempt = now + IntegrationRetryRules.ClaimLease;
        }

        context.StampOnOtherTenantsBehalf();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (Outcome.Skipped, null);
        }

        await transaction.CommitAsync(CancellationToken.None);
        if (row.NextAttempt is null)
        {
            this.log.LogError("The notice {MessageId} of tenant {TenantId} was taken {Attempts} times without being delivered; it is not delivered and tried no more.", row.Id, row.TenantId, row.Attempts);
            return (Outcome.DeadLettered, null);
        }

        return (Outcome.Skipped, new NoticeClaim(row.Id, row.TenantId, row.Attempts, priorStatus, priorNext));
    }

    private async Task<Outcome> UpdateClaimedNoticeAsync(NoticeClaim claim, Func<OutgoingMessage, Outcome> change, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = this.dbFactory.CreateSystemDbContext();
            using var lease = context.BeginSystemScope(NoticeReason);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            await context.HoldTenantAsync(claim.TenantId, CancellationToken.None);
            var (id, tenantId, attempts) = (claim.Id, claim.TenantId, claim.Attempts);
            var row = await context.OutgoingMessages.AsTracking()
                .FirstOrDefaultAsync(notice => notice.Id == id && notice.TenantId == tenantId && notice.Attempts == attempts && notice.Status == OutgoingMessageStatus.Pending, CancellationToken.None);
            if (row is null)
            {
                return Outcome.Skipped;
            }

            var outcome = change(row);
            context.StampOnOtherTenantsBehalf();
            await context.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return outcome;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Outcome.Skipped;
        }
    }
}

public sealed class IntegrationOutboxService : BackgroundService
{
    private readonly IntegrationOutboxDispatcher dispatcher;
    private readonly IOptions<IntegrationOutboxOptions> options;
    private readonly TimeProvider clock;
    private readonly ILogger<IntegrationOutboxService> log;

    public IntegrationOutboxService(IntegrationOutboxDispatcher dispatcher, IOptions<IntegrationOutboxOptions> options, TimeProvider clock, ILogger<IntegrationOutboxService> log)
    {
        this.dispatcher = dispatcher;
        this.options = options;
        this.clock = clock;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = this.options.Value;
        if (!settings.Enabled)
        {
            this.log.LogInformation("The integration outbox's dispatcher is off ({Section}:{Setting}); no integration message is delivered and no notice delivered again.", IntegrationOutboxOptions.Section, nameof(IntegrationOutboxOptions.Enabled));
            return;
        }

        this.log.LogInformation("The integration outbox's dispatcher runs every {Interval}.", settings.Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var pass = StafforaTelemetry.Source.StartActivity(StafforaTelemetry.OutboxPassActivity))
            {
                try
                {
                    await this.dispatcher.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    pass?.SetStatus(ActivityStatusCode.Error);
                    this.log.LogError(exception, "A pass of the integration outbox's dispatcher failed; the next one tries again.");
                }
            }

            try
            {
                await Task.Delay(settings.Interval, this.clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
