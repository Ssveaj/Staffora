using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Messaging;

namespace Staffora.Core.Entities.Messaging;

public class OutgoingMessage : TenantEntityBase
{
    public const int MaxNamedRecipients = 100;

    public string MessageType { get; set; } = string.Empty;

    public MessageCategory Category { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public Guid HistoryEntryId { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? Link { get; set; }

    public List<Guid> RecipientUserIds { get; set; } = [];

    public RolePermission? RecipientPermission { get; set; }

    public RolePermission? NamedRecipientsHold { get; set; }

    public RolePermission? FallbackPermission { get; set; }

    public OutgoingMessageStatus Status { get; set; }

    public DateTime? Delivered { get; set; }

    public int RecipientCount { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttempt { get; set; }
}
