using Staffora.Core.Messaging;

namespace Staffora.Core.Entities.Messaging;

public class StoredMessage : TenantEntityBase
{
    public Guid OutgoingMessageId { get; set; }

    public OutgoingMessage? OutgoingMessage { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public MessageCategory Category { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? Link { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public Guid HistoryEntryId { get; set; }

    public ICollection<MessageRecipient> Recipients { get; set; } = [];
}
