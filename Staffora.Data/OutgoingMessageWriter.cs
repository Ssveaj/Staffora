using Staffora.Core.Entities;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;

namespace Staffora.Data;

public static class OutgoingMessageWriter
{
    public static IReadOnlyList<OutgoingMessage> Add(ApplicationDbContext context, TransitionOutcome outcome, Guid historyEntryId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.OutgoingMessages.Count == 0 || outcome.Steps.Count == 0)
        {
            return [];
        }

        var primary = outcome.Steps[0].Entity;
        var messages = outcome.OutgoingMessages.Distinct().ToList();
        foreach (var message in messages)
        {
            if (message.TenantId == Guid.Empty)
            {
                message.TenantId = TenantOf(primary);
            }

            if (string.IsNullOrEmpty(message.EntityType))
            {
                message.EntityType = (context.Model.FindRuntimeEntityType(primary.GetType())?.ClrType ?? primary.GetType()).Name;
            }

            if (message.EntityId == Guid.Empty)
            {
                message.EntityId = primary.Id;
            }

            message.HistoryEntryId = historyEntryId;
            message.Status = OutgoingMessageStatus.Pending;
            message.Delivered = null;
            message.RecipientCount = 0;
        }

        context.OutgoingMessages.AddRange(messages);
        return messages;
    }

    public static IReadOnlyList<OutgoingMessage> AddForComment(ApplicationDbContext context, IReadOnlyList<OutgoingMessage> messages, Guid commentId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(messages);
        var added = messages.Distinct().ToList();
        foreach (var message in added)
        {
            if (message.TenantId == Guid.Empty || string.IsNullOrEmpty(message.EntityType) || message.EntityId == Guid.Empty)
            {
                throw new InvalidOperationException($"A comment's notice of \"{message.MessageType}\" names its tenant and entity before it is written.");
            }

            message.HistoryEntryId = commentId;
            message.Status = OutgoingMessageStatus.Pending;
            message.Delivered = null;
            message.RecipientCount = 0;
        }

        context.OutgoingMessages.AddRange(added);
        return added;
    }

    public static OutgoingMessage AddOnBehalfOf(ApplicationDbContext context, Guid tenantId, OutgoingMessage message, Guid historyEntryId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(message);
        if (message.TenantId != tenantId || string.IsNullOrEmpty(message.EntityType) || message.EntityId == Guid.Empty)
        {
            throw new InvalidOperationException($"A notice of \"{message.MessageType}\" written on a tenant's behalf names that tenant and its entity before it is written.");
        }

        message.HistoryEntryId = historyEntryId;
        message.Status = OutgoingMessageStatus.Pending;
        message.Delivered = null;
        message.RecipientCount = 0;
        message.CreatedById = SystemAccount.IdFor(tenantId);
        context.OutgoingMessages.Add(message);
        return message;
    }

    private static Guid TenantOf(IStateEntity entity) => entity switch
    {
        ITenantEntity row => row.TenantId,
        Tenant tenant => tenant.Id,
        _ => Guid.Empty,
    };
}
