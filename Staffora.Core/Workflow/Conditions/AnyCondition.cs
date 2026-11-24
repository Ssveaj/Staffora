namespace Staffora.Core.Workflow.Conditions;

public sealed class AnyCondition<TState, TEntity> : Condition<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    public AnyCondition(string description, params Condition<TState, TEntity>[] conditions) : base(description, conditions.SelectMany(c => c.Reads))
    {
        Conditions = [.. conditions];
    }

    public IReadOnlyList<Condition<TState, TEntity>> Conditions { get; }

    public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<TState, TEntity> context) =>
        IsMet(context) ? [] : [Description];

    protected override bool IsMet(WorkflowContext<TState, TEntity> context) =>
        Conditions.Any(c => c.GetBlockingReasons(context).Count == 0);
}
