using Staffora.Core.Entities.Identity;

namespace Staffora.Core.Entities.Messaging;

public class MessageRecipient : TenantEntityBase
{
    public Guid StoredMessageId { get; set; }

    public StoredMessage? StoredMessage { get; set; }

    public Guid UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAt { get; set; }
}
