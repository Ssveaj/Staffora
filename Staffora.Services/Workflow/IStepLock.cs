using Staffora.Data;

namespace Staffora.Services.Workflow;

public interface IStepLock
{
    Type EntityType { get; }

    Task TakeAsync(ApplicationDbContext context, Guid? entityId, CancellationToken cancellationToken);
}
