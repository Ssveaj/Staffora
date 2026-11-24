using System.Linq.Expressions;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow;

public sealed class WorkflowState<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly Workflow<TState, TEntity> workflow;
    private readonly List<IAction<TState, TEntity>> enterActions = [];
    private readonly List<IAction<TState, TEntity>> leaveActions = [];

    internal WorkflowState(Workflow<TState, TEntity> workflow, TState state)
    {
        this.workflow = workflow;
        State = state;
    }

    public TState State { get; }

    public StateFieldConfig<TEntity> Fields { get; } = new();

    public IReadOnlyList<IAction<TState, TEntity>> EnterActions => this.enterActions;

    public IReadOnlyList<IAction<TState, TEntity>> LeaveActions => this.leaveActions;

    public WorkflowState<TState, TEntity> ConfigureFields(Action<StateFieldBuilder<TEntity>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.workflow.EnsureNotRegistered();
        configure(new StateFieldBuilder<TEntity>(Fields));
        return this;
    }

    public WorkflowState<TState, TEntity> AddEnterAction(IAction<TState, TEntity> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        this.workflow.EnsureNotRegistered();
        this.enterActions.Add(action);
        return this;
    }

    public WorkflowState<TState, TEntity> AddLeaveAction(IAction<TState, TEntity> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        this.workflow.EnsureNotRegistered();
        this.leaveActions.Add(action);
        return this;
    }

    public WorkflowState<TState, TEntity> Cascade<TChildState, TChild>(Expression<Func<TEntity, IEnumerable<TChild>?>> navigation, TChildState target, params TChildState[] leaveAlone)
        where TChildState : struct, Enum
        where TChild : class, IStateEntity<TChildState> =>
        AddEnterAction(new CascadeAction<TState, TEntity, TChildState, TChild>(navigation, target, leaveAlone));
}
