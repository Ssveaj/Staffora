using Microsoft.EntityFrameworkCore;

namespace Staffora.Core.Messaging;

public interface IHandledNotices
{
    Task<IReadOnlyList<Guid>> MarkReadAsync(DbContext store, Guid tenantId, Type entityType, Guid entityId, IReadOnlyCollection<MessageType> types, CancellationToken cancellationToken);

    void TellReaders(Guid tenantId, IReadOnlyCollection<Guid> readers);
}
