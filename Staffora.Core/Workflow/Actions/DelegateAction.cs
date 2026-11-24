namespace Staffora.Core.Workflow.Actions;

public sealed class DelegateAction<TState, TEntity> : IAction<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly Func<WorkflowContext<TState, TEntity>, CancellationToken, Task> execute;
    private readonly Func<WorkflowContext<TState, TEntity>, IEnumerable<IStateEntity>>? touches;

    public DelegateAction(Func<WorkflowContext<TState, TEntity>, CancellationToken, Task> execute, Func<WorkflowContext<TState, TEntity>, IEnumerable<IStateEntity>>? touches = null)
    {
        this.touches = touches;
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public DelegateAction(Action<WorkflowContext<TState, TEntity>> execute, Func<WorkflowContext<TState, TEntity>, IEnumerable<IStateEntity>>? touches = null)
        : this(Wrap(execute), touches)
    {
    }

    public Task ExecuteAsync(WorkflowContext<TState, TEntity> context, CancellationToken cancellationToken) => this.execute(context, cancellationToken);

    public void AddLockCandidates(WorkflowContext<TState, TEntity> context, LockCandidates candidates)
    {
        if (this.touches is not null)
        {
            candidates.AddRange(this.touches(context));
        }
    }

    private static Func<WorkflowContext<TState, TEntity>, CancellationToken, Task> Wrap(Action<WorkflowContext<TState, TEntity>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return (context, _) =>
        {
            execute(context);
            return Task.CompletedTask;
        };
    }
}
