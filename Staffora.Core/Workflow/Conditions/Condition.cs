namespace Staffora.Core.Workflow.Conditions;

public abstract class Condition<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    protected Condition(string description, IEnumerable<string>? reads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        Reads = reads?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
    }

    public string Description { get; }

    public IReadOnlyList<string> Reads { get; }

    public virtual IReadOnlyList<string> GetBlockingReasons(WorkflowContext<TState, TEntity> context)
    {
        var missing = Reads.Where(path => !context.IsNavigationLoaded(path)).ToList();
        if (missing.Count > 0)
        {
            return [$"{Description} (cannot be checked: {string.Join(", ", missing)} not loaded)"];
        }

        return IsMet(context) ? [] : [Description];
    }


    protected abstract bool IsMet(WorkflowContext<TState, TEntity> context);
}
