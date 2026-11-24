namespace Staffora.Core.Workflow;

public interface IStateEntity
{
    Guid Id { get; set; }

    bool IsInTransition { get; set; }
}

public interface IStateEntity<TState> : IStateEntity
    where TState : struct, Enum
{
    TState State { get; set; }
}
