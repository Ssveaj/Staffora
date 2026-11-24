namespace Staffora.Core.Workflow.Actions;

public interface IAction<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    Task ExecuteAsync(WorkflowContext<TState, TEntity> context, CancellationToken cancellationToken);

    void AddLockCandidates(WorkflowContext<TState, TEntity> context, LockCandidates candidates);
}

internal interface IBlockingCheck<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    IEnumerable<string> GetBlockingReasons(WorkflowContext<TState, TEntity> context, ISet<IStateEntity> moving);
}

internal interface IRegistrationCheck
{
    void CheckAtRegistration(string declaredAt, WorkflowRegistry registry);
}
