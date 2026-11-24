using Staffora.Core.Entities.Integration;

namespace Staffora.Data;

public static class IntegrationOutbox
{
    public static IntegrationMessage Enqueue(ApplicationDbContext context, string kind, Guid targetTenantId, Guid correlationId, string payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(kind.Length, IntegrationMessage.MaxKindLength, nameof(kind));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, IntegrationMessage.MaxPayloadLength, nameof(payload));
        if (targetTenantId == Guid.Empty || correlationId == Guid.Empty)
        {
            throw new ArgumentException("An integration message names its target tenant and its correlation.");
        }

        if (context.IsInSystemScope)
        {
            throw new InvalidOperationException("An integration message is queued in its sending tenant's own step, never under a lease, where the runtime's tenant need not be the sender.");
        }

        var sender = context.CurrentTenantId ?? throw new InvalidOperationException("An integration message is sent by the runtime's tenant, and this context has none.");
        if (sender == targetTenantId)
        {
            throw new ArgumentException("A tenant sends no integration message to itself.", nameof(targetTenantId));
        }

        var message = new IntegrationMessage
        {
            TenantId = sender,
            Kind = kind,
            TargetTenantId = targetTenantId,
            CorrelationId = correlationId,
            Payload = payload,
            State = IntegrationMessageState.Pending,
            NextAttempt = context.Clock.GetUtcNow().UtcDateTime,
        };
        context.IntegrationMessages.Add(message);
        return message;
    }
}
