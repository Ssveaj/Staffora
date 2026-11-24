namespace Staffora.Core.Workflow;

public abstract class StateEntityBase<TState> : IStateEntity<TState>
    where TState : struct, Enum
{
    public Guid Id { get; set; }

    public TState State { get; set; }

    public bool IsInTransition { get; set; }

    public DateTime Created { get; set; }

    public Guid CreatedById { get; set; }
}
