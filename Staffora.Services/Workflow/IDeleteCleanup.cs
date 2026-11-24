using Staffora.Core.Workflow;
using Staffora.Data;

namespace Staffora.Services.Workflow;

public interface IDeleteCleanup
{
    Type EntityType { get; }

    IReadOnlyList<Type> Dependents { get; }

    Task BeforeRemoveAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken);
}
