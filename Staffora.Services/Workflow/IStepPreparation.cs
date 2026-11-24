using Staffora.Data;

namespace Staffora.Services.Workflow;

public interface IStepPreparation
{
    Type EntityType { get; }

    Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken);

    Task PrepareNewAsync(ApplicationDbContext context, object entity, CancellationToken cancellationToken) => Task.CompletedTask;

    Task PrepareEditedAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    void ForgetEdited(Guid entityId)
    {
    }
}
