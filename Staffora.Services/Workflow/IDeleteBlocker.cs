using Staffora.Core.Workflow;
using Staffora.Data;

namespace Staffora.Services.Workflow;

public interface IDeleteBlocker
{
    Type EntityType { get; }

    IReadOnlyList<Type> Dependents { get; }

    Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken);
}
