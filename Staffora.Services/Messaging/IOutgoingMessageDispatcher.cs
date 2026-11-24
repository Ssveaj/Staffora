using Staffora.Core.Entities.Messaging;

namespace Staffora.Services.Messaging;

public interface IOutgoingMessageDispatcher
{
    Task DispatchAsync(IReadOnlyList<OutgoingMessage> messages, Guid historyEntryId, CancellationToken cancellationToken);
}
