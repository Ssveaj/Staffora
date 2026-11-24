using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Messaging;
using Staffora.Data;

namespace Staffora.Services.Messaging;

public sealed class HandledNotices : IHandledNotices
{
    private readonly NoticeBus bus;
    private readonly TimeProvider clock;
    private readonly ILogger<HandledNotices> log;

    public HandledNotices(NoticeBus bus, TimeProvider clock, ILogger<HandledNotices> log)
    {
        this.bus = bus;
        this.clock = clock;
        this.log = log;
    }

    public async Task<IReadOnlyList<Guid>> MarkReadAsync(DbContext store, Guid tenantId, Type entityType, Guid entityId, IReadOnlyCollection<MessageType> types, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(types);
        if (store is not ApplicationDbContext context || context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The notices a step settles are marked read inside that step, on its own context and transaction.");
        }

        if (tenantId == Guid.Empty || entityId == Guid.Empty || types.Count == 0)
        {
            return [];
        }

        var name = CommentThreadWriter.EntityTypeOf(context, entityType);
        var keys = types.Select(type => type.Key).Distinct(StringComparer.Ordinal).ToList();
        var unread = context.MessageRecipients.Where(copy => copy.TenantId == tenantId
            && !copy.IsRead
            && copy.StoredMessage!.TenantId == tenantId
            && copy.StoredMessage.EntityType == name
            && copy.StoredMessage.EntityId == entityId
            && keys.Contains(copy.StoredMessage.MessageType));
        var readers = await unread.Select(copy => copy.UserId).Distinct().ToListAsync(cancellationToken);
        if (readers.Count == 0)
        {
            return [];
        }

        var now = this.clock.GetUtcNow().UtcDateTime;
        var editor = context.ActorIn(tenantId);
        await unread.ExecuteUpdateAsync(
            set => set
                .SetProperty(copy => copy.IsRead, true)
                .SetProperty(copy => copy.ReadAt, now)
                .SetProperty(copy => copy.Modified, now)
                .SetProperty(copy => copy.ModifiedById, editor),
            cancellationToken);
        return readers;
    }

    public void TellReaders(Guid tenantId, IReadOnlyCollection<Guid> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        foreach (var reader in readers.Distinct())
        {
            try
            {
                this.bus.Publish(tenantId, reader, NoticeArrived.Read);
            }
            catch (Exception exception)
            {
                this.log.LogWarning(exception, "Telling user {UserId}'s open pages that their notices were marked read failed; their bell reads the count again when it next opens.", reader);
            }
        }
    }
}
