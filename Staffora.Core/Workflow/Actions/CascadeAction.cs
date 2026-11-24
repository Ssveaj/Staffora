using System.Linq.Expressions;

namespace Staffora.Core.Workflow.Actions;

public sealed class CascadeAction<TState, TEntity, TChildState, TChild> : IAction<TState, TEntity>, ICascadeAction, IStateCascade<TState, TEntity>, IBlockingCheck<TState, TEntity>, IRegistrationCheck
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
    where TChildState : struct, Enum
    where TChild : class, IStateEntity<TChildState>
{
    private readonly IReadOnlyList<System.Reflection.PropertyInfo> path;
    private WorkflowRegistry? registry;

    public CascadeAction(Expression<Func<TEntity, IEnumerable<TChild>?>> navigation, TChildState target, IEnumerable<TChildState>? leaveAlone = null)
    {
        this.path = MemberPaths.PathOf(navigation);
        Navigation = string.Join('.', this.path.Select(property => property.Name));
        Target = target;
        LeaveAlone = leaveAlone?.Distinct().ToArray() ?? [];
    }

    public string Navigation { get; }

    public bool ThroughReference => this.path.Count > 1;

    public TChildState Target { get; }

    public IReadOnlyList<TChildState> LeaveAlone { get; }

    Type ICascadeAction.ChildType => typeof(TChild);

    Enum ICascadeAction.Target => Target;

    IReadOnlyList<Enum> ICascadeAction.LeaveAlone => [.. LeaveAlone.Select(state => (Enum)state)];

    public Task ExecuteAsync(WorkflowContext<TState, TEntity> context, CancellationToken cancellationToken) =>
        ((IStateCascade<TState, TEntity>)this).RunAsync(context.Entity, context.Environment, context.Comment, context.Outcome, cancellationToken);

    public void AddLockCandidates(WorkflowContext<TState, TEntity> context, LockCandidates candidates) =>
        ((IStateCascade<TState, TEntity>)this).AddCandidates(context.Entity, context.Environment, candidates);

    IEnumerable<string> IBlockingCheck<TState, TEntity>.GetBlockingReasons(WorkflowContext<TState, TEntity> context, ISet<IStateEntity> moving) =>
        ((IStateCascade<TState, TEntity>)this).BlockingReasons(context.Entity, context.Environment, context.Outcome, moving);

    async Task IStateCascade<TState, TEntity>.RunAsync(TEntity parent, WorkflowEnvironment environment, string? comment, TransitionOutcome outcome, CancellationToken cancellationToken)
    {
        var workflow = ChildWorkflow();

        foreach (var child in AlreadyDeleted(parent, environment))
        {
            if (outcome.RecordAlreadyInDeleteState(child, child.State))
            {
                await workflow.CascadeInPlaceAsync(child, environment, comment, outcome, cancellationToken);
            }
        }

        foreach (var (child, transition) in PlanOrRefuse(parent, environment, workflow))
        {
            if (EqualityComparer<TChildState>.Default.Equals(child.State, Target))
            {
                continue;
            }

            await workflow.ExecuteAsync(child, transition, environment, comment, outcome, cancellationToken);
        }
    }

    void IStateCascade<TState, TEntity>.AddCandidates(TEntity parent, WorkflowEnvironment environment, LockCandidates candidates)
    {
        var workflow = ChildWorkflow();

        foreach (var child in AlreadyDeleted(parent, environment))
        {
            candidates.Add(child);
            workflow.AddInPlaceCandidates(child, environment, candidates);
        }

        foreach (var (child, transition) in PlanOrRefuse(parent, environment, workflow))
        {
            workflow.AddCandidates(child, transition, environment, candidates);
        }
    }

    IEnumerable<string> IStateCascade<TState, TEntity>.BlockingReasons(TEntity parent, WorkflowEnvironment environment, TransitionOutcome outcome, ISet<IStateEntity> moving)
    {
        var workflow = ChildWorkflow();
        var (plan, reasons) = Plan(parent, environment, workflow);
        foreach (var child in AlreadyDeleted(parent, environment))
        {
            reasons.AddRange(workflow.InPlaceBlockingReasons(child, environment, outcome, moving));
        }

        foreach (var (child, transition) in plan)
        {
            reasons.AddRange(workflow.BlockingReasons(child, transition, environment, outcome, moving));
        }

        return reasons.Distinct(StringComparer.Ordinal);
    }

    void IRegistrationCheck.CheckAtRegistration(string declaredAt, WorkflowRegistry registry)
    {
        var childName = typeof(TChild).Name;
        if (registry.GetDefinition(typeof(TChild)) is not Workflow<TChildState, TChild> workflow)
        {
            throw new WorkflowDefinitionException($"The cascade at {declaredAt} names {childName}, whose workflow is not a Workflow<{typeof(TChildState).Name}, {childName}>.");
        }

        if (!workflow.HasState(Target))
        {
            throw new WorkflowDefinitionException($"The cascade at {declaredAt} moves {childName} to {Target}, which the {childName} workflow does not register.");
        }

        var edges = workflow.Transitions.Where(t => EqualityComparer<TChildState>.Default.Equals(t.To, Target) && !t.IsSelfTransition && !t.NeverCascaded).ToList();
        if (edges.Count == 0)
        {
            throw new WorkflowDefinitionException($"The cascade at {declaredAt} moves {childName} to {Target}, but the {childName} workflow has no transition into {Target}.");
        }

        var ambiguous = edges.GroupBy(t => t.From).FirstOrDefault(group => group.Count() > 1);
        if (ambiguous is not null)
        {
            throw new WorkflowDefinitionException($"The cascade at {declaredAt} moves {childName} to {Target}, but {ambiguous.Key} has {ambiguous.Count()} transitions into it.");
        }

        if (ThroughReference && Target.IsDeleteState())
        {
            throw new WorkflowDefinitionException($"The cascade at {declaredAt} moves {childName} through {Navigation} to the delete state {Target}; a cascade through a reference never deletes the records it reaches.");
        }

        var unknown = LeaveAlone.Where(state => !workflow.HasState(state) || EqualityComparer<TChildState>.Default.Equals(state, Target)).ToList();
        if (unknown.Count > 0)
        {
            throw new WorkflowDefinitionException($"The cascade at {declaredAt} leaves {childName} alone in {string.Join(", ", unknown)}, which is the target or not a registered state.");
        }

        if (Target.IsDeleteState() && LeaveAlone.Count > 0)
        {
            throw new WorkflowDefinitionException($"The cascade at {declaredAt} moves {childName} to the delete state {Target}, so it cannot leave {childName} alone in {string.Join(", ", LeaveAlone)}: a child left behind would still refer to the removed parent. Block the delete with a condition instead.");
        }

        this.registry = registry;
    }

    private List<TChild> AlreadyDeleted(TEntity parent, WorkflowEnvironment environment) =>
        Target.IsDeleteState() && WorkflowContext<TState, TEntity>.IsNavigationLoaded(parent, environment, Navigation)
            ? [.. ChildrenOf(parent).Where(child => EqualityComparer<TChildState>.Default.Equals(child.State, Target))]
            : [];

    private IEnumerable<TChild> ChildrenOf(TEntity parent)
    {
        object? current = parent;
        foreach (var property in this.path)
        {
            if (current is null)
            {
                return [];
            }

            current = property.GetValue(current);
        }

        return current is not IEnumerable<TChild> children ? []
            : ThroughReference ? children.Where(child => !ReferenceEquals(child, parent) && !(child is TEntity && child.Id == parent.Id))
            : children;
    }

    private Workflow<TChildState, TChild> ChildWorkflow() =>
        (this.registry ?? throw new InvalidOperationException($"The cascade to {typeof(TChild).Name} runs only in a workflow obtained from a {nameof(WorkflowRegistry)}."))
            .Get<TChildState, TChild>();

    private List<(TChild Child, Transition<TChildState, TChild> Transition)> PlanOrRefuse(TEntity parent, WorkflowEnvironment environment, Workflow<TChildState, TChild> workflow)
    {
        var (plan, reasons) = Plan(parent, environment, workflow);
        return reasons.Count == 0 ? plan : throw new TransitionRefusedException(reasons);
    }

    private (List<(TChild Child, Transition<TChildState, TChild> Transition)> Plan, List<string> Reasons) Plan(TEntity parent, WorkflowEnvironment environment, Workflow<TChildState, TChild> workflow)
    {
        var plan = new List<(TChild, Transition<TChildState, TChild>)>();
        if (!WorkflowContext<TState, TEntity>.IsNavigationLoaded(parent, environment, Navigation))
        {
            return (plan, [$"{typeof(TEntity).Name}.{Navigation} is not loaded, so its {typeof(TChild).Name} children cannot be moved to {Target}."]);
        }

        var reasons = new List<string>();
        foreach (var child in ChildrenOf(parent))
        {
            if (EqualityComparer<TChildState>.Default.Equals(child.State, Target) || LeaveAlone.Contains(child.State))
            {
                continue;
            }

            if (workflow.GetTransitions(child.State, Target).Where(candidate => !candidate.NeverCascaded).SingleOrDefault() is { } transition)
            {
                plan.Add((child, transition));
            }
            else
            {
                reasons.Add($"{typeof(TChild).Name} {child.Id} is {child.State} and cannot move to {Target}.");
            }
        }

        return (plan, reasons);
    }
}

public interface ICascadeAction
{
    string Navigation { get; }

    Type ChildType { get; }

    Enum Target { get; }

    IReadOnlyList<Enum> LeaveAlone { get; }
}

internal interface IStateCascade<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    Task RunAsync(TEntity parent, WorkflowEnvironment environment, string? comment, TransitionOutcome outcome, CancellationToken cancellationToken);

    void AddCandidates(TEntity parent, WorkflowEnvironment environment, LockCandidates candidates);

    IEnumerable<string> BlockingReasons(TEntity parent, WorkflowEnvironment environment, TransitionOutcome outcome, ISet<IStateEntity> moving);
}
