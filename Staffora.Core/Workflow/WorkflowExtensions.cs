namespace Staffora.Core.Workflow;

public static class WorkflowExtensions
{
    public static Workflow<TState, TEntity> GetWorkflow<TState, TEntity>(this TEntity entity)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentNullException.ThrowIfNull(entity);
        return WorkflowRegistry.Default.Get<TState, TEntity>();
    }
}
