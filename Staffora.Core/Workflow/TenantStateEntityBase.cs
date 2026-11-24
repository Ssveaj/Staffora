using Staffora.Core.Entities;

namespace Staffora.Core.Workflow;

public abstract class TenantStateEntityBase<TState> : TenantEntityBase, IStateEntity<TState>
    where TState : struct, Enum
{
    public TState State { get; set; }

    public bool IsInTransition { get; set; }
}
