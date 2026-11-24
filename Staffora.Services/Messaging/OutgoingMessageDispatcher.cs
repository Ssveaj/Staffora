using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Services.Messaging;

public sealed class OutgoingMessageDispatcher : IOutgoingMessageDispatcher
{
    private readonly MessageChannelRouter router;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly TimeProvider clock;
    private readonly ILogger<OutgoingMessageDispatcher> log;

    public OutgoingMessageDispatcher(MessageChannelRouter router, IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, TimeProvider clock, ILogger<OutgoingMessageDispatcher> log)
    {
        this.router = router;
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.clock = clock;
        this.log = log;
    }

    public async Task DispatchAsync(IReadOnlyList<OutgoingMessage> messages, Guid historyEntryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages)
        {
            try
            {
                var (held, state) = await TenantHoldsAsync(message, cancellationToken);
                if (held)
                {
                    this.log.LogWarning("Held the notice {MessageId} ({MessageType}) of history entry {HistoryEntryId}: its tenant is {TenantState}, so it stays pending and is sent on no channel.", message.Id, message.MessageType, historyEntryId, state?.ToString() ?? "not found");
                    continue;
                }

                foreach (var sender in this.router.SendersOf(message))
                {
                    await sender.SendAsync(message, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, "Delivering the notice {MessageId} ({MessageType}) of history entry {HistoryEntryId} failed; it is marked not delivered.", message.Id, message.MessageType, historyEntryId);
                await MarkFailedAsync(message);
            }
        }
    }

    private async Task<(bool Held, TenantState? State)> TenantHoldsAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScopeFor(this.runtime, message.TenantId, "read the state of the tenant a notice goes into");
        var tenantId = message.TenantId;
        var state = await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => (TenantState?)tenant.State).FirstOrDefaultAsync(cancellationToken);
        return (TenantStates.HoldsData(state), state);
    }

    private async Task MarkFailedAsync(OutgoingMessage message)
    {
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScopeFor(this.runtime, message.TenantId, "mark a notice a step wrote into a tenant not delivered");
            var (id, tenantId) = (message.Id, message.TenantId);
            var row = await context.OutgoingMessages.FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.TenantId == tenantId && candidate.Status == OutgoingMessageStatus.Pending, CancellationToken.None);
            if (row is null)
            {
                return;
            }

            row.Status = OutgoingMessageStatus.Failed;
            row.Attempts++;
            row.NextAttempt = IntegrationRetryRules.IsSpent(row.Attempts) ? null : this.clock.GetUtcNow().UtcDateTime + IntegrationRetryRules.RetryAfter(row.Attempts);
            context.StampOnOtherTenantsBehalf();

            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            this.log.LogError(exception, "Marking the notice {MessageId} not delivered failed as well; it stays pending.", message.Id);
        }
    }
}
