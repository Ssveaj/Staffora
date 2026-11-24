namespace Staffora.Core.Entities.Portal;

public interface IOrderSender
{
    void Link(Order request);

    Task DeliverAsync(Guid requestId, CancellationToken cancellationToken);

    Task QueueCancelAsync(Microsoft.EntityFrameworkCore.DbContext store, Order request, CancellationToken cancellationToken);
}
