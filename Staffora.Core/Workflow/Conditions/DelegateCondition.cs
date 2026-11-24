namespace Staffora.Core.Workflow.Conditions;

public sealed class DelegateCondition<TState, TEntity> : Condition<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly Func<WorkflowContext<TState, TEntity>, bool> predicate;

    public DelegateCondition(string description, Func<TEntity, bool> predicate, params string[] reads)
        : this(description, reads, predicate is null ? null! : context => predicate(context.Entity))
    {
    }

    private DelegateCondition(string description, IEnumerable<string> reads, Func<WorkflowContext<TState, TEntity>, bool> predicate)
        : base(description, reads)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        this.predicate = predicate;
    }

    public static DelegateCondition<TState, TEntity> FromContext(string description, Func<WorkflowContext<TState, TEntity>, bool> predicate, params string[] reads) =>
        new(description, reads, predicate);

    protected override bool IsMet(WorkflowContext<TState, TEntity> context) => this.predicate(context);
}
