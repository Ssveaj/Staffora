using Staffora.Core.Workflow;

namespace Staffora.Services.Workflow;

public sealed record TransitionAvailability<TState, TEntity>(Transition<TState, TEntity> Transition, string? AccessRefusal, IReadOnlyList<string> BlockingReasons)
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    public bool MayRun => AccessRefusal is null;

    public bool CanRun => MayRun && BlockingReasons.Count == 0;
}
