using System.Diagnostics.CodeAnalysis;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow;

public sealed class Workflow<TState, TEntity> : IWorkflow, IRegistrable
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly Dictionary<TState, WorkflowState<TState, TEntity>> states = [];
    private readonly List<Transition<TState, TEntity>> transitions = [];
    private readonly Dictionary<string, Transition<TState, TEntity>> transitionsByKey = new(StringComparer.Ordinal);
    private volatile bool isRegistered;

    public Type EntityType => typeof(TEntity);

    public Type StateType => typeof(TState);

    public bool IsRegistered => this.isRegistered;

    public IReadOnlyCollection<WorkflowState<TState, TEntity>> States => this.states.Values;

    public IReadOnlyList<Transition<TState, TEntity>> Transitions => this.transitions;

    IReadOnlyList<Enum> IWorkflow.States => this.states.Keys.Select(state => (Enum)state).ToList();

    IReadOnlyList<ITransition> IWorkflow.Transitions => this.transitions;

    public WorkflowState<TState, TEntity> this[TState state] =>
        this.states.TryGetValue(state, out var workflowState)
            ? workflowState
            : throw new KeyNotFoundException($"{typeof(TEntity).Name} has no state {state} in its workflow.");

    public bool HasState(TState state) => this.states.ContainsKey(state);

    public WorkflowState<TState, TEntity> AddState(TState state)
    {
        EnsureNotRegistered();
        var workflowState = new WorkflowState<TState, TEntity>(this, state);
        if (!this.states.TryAdd(state, workflowState))
        {
            throw new WorkflowDefinitionException($"{typeof(TEntity).Name} registers state {state} twice.");
        }

        return workflowState;
    }

    public Transition<TState, TEntity> AddTransition(Transition<TState, TEntity> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        EnsureNotRegistered();
        if (transition.Workflow is not null)
        {
            throw new WorkflowDefinitionException($"Transition '{transition.Key}' already belongs to a workflow.");
        }

        if (!this.transitionsByKey.TryAdd(transition.Key, transition))
        {
            throw new WorkflowDefinitionException($"{typeof(TEntity).Name} declares transition key '{transition.Key}' twice.");
        }

        transition.Workflow = this;
        this.transitions.Add(transition);
        return transition;
    }

    public Transition<TState, TEntity> GetTransition(string key) =>
        TryGetTransition(key, out var transition)
            ? transition
            : throw new KeyNotFoundException($"{typeof(TEntity).Name} has no transition '{key}'.");

    public bool TryGetTransition(string key, [MaybeNullWhen(false)] out Transition<TState, TEntity> transition) =>
        this.transitionsByKey.TryGetValue(key, out transition);

    public bool IsDeleteStep(string key) =>
        TryGetTransition(key, out var transition) && ((Enum)(object)transition.To).IsDeleteState();

    public IEnumerable<Transition<TState, TEntity>> GetTransitions(TState from, TState to) =>
        this.transitions.Where(t => EqualityComparer<TState>.Default.Equals(t.From, from) && EqualityComparer<TState>.Default.Equals(t.To, to));

    public IEnumerable<Transition<TState, TEntity>> GetTransitionsFrom(TState from) =>
        this.transitions.Where(t => EqualityComparer<TState>.Default.Equals(t.From, from)).OrderBy(t => t.SortOrder);

    public StateFieldConfig<TEntity> GetFieldConfig(TState state) => this[state].Fields;

    public StateFieldConfig<TEntity> GetFieldConfig(TEntity entity) => GetFieldConfig(entity.State);

    FieldAccessFlags IWorkflow.GetFieldAccess(Enum state, string field) =>
        state is TState typed
            ? GetFieldConfig(typed).GetFieldAccess(field)
            : throw new ArgumentException($"{state.GetType().Name}.{state} is not a {typeof(TState).Name}.", nameof(state));

    bool IWorkflow.DeclaresField(string field) => this.states.Values.Any(state => state.Fields.Declares(field));

    IReadOnlyList<ICascadeAction> IWorkflow.GetCascades(string transitionKey) => [.. ActionsOf(GetTransition(transitionKey)).OfType<ICascadeAction>()];

    IReadOnlyList<ICascadeAction> IWorkflow.GetStateCascades(Enum state) =>
        state is TState typed
            ? [.. StateCascades(typed).OfType<ICascadeAction>()]
            : throw new ArgumentException($"{state.GetType().Name}.{state} is not a {typeof(TState).Name}.", nameof(state));

    ITransition? IWorkflow.GetSaveStep(Enum state) =>
        state is TState typed
            ? GetSaveStep(typed)
            : throw new ArgumentException($"{state.GetType().Name}.{state} is not a {typeof(TState).Name}.", nameof(state));

    public Transition<TState, TEntity>? GetSaveStep(TState state) =>
        this.transitions.FirstOrDefault(transition => transition.IsSave && EqualityComparer<TState>.Default.Equals(transition.From, state));

    ITransition? IWorkflow.GetEditAuthority(Enum state) =>
        state is TState typed
            ? GetEditAuthority(typed)
            : throw new ArgumentException($"{state.GetType().Name}.{state} is not a {typeof(TState).Name}.", nameof(state));

    public Transition<TState, TEntity>? GetEditAuthority(TState state)
    {
        if (GetSaveStep(state) is { } save)
        {
            return save;
        }

        var saves = EditCarryingSteps(state).Where(transition => transition.IsSelfTransition).Take(2).ToList();
        return saves is [var only] ? only : null;
    }

    bool IWorkflow.EditsGoWithItsSteps(Enum state) =>
        state is TState typed
            ? EditsGoWithItsSteps(typed)
            : throw new ArgumentException($"{state.GetType().Name}.{state} is not a {typeof(TState).Name}.", nameof(state));

    public bool EditsGoWithItsSteps(TState state)
    {
        if (GetEditAuthority(state) is not null)
        {
            return false;
        }

        var gates = EditCarryingSteps(state).Select(transition => (transition.RequiredPermission, transition.RequiresPlatformOwner)).Distinct().Take(2).ToList();
        return gates.Count == 1;
    }

    private IEnumerable<Transition<TState, TEntity>> EditCarryingSteps(TState state) =>
        this.transitions.Where(transition => EqualityComparer<TState>.Default.Equals(transition.From, state)
            && !transition.IsSystemDriven && !transition.TakesNoEdits && !((Enum)(object)transition.To).IsDeleteState());

    IReadOnlyList<ITransition> IWorkflow.StepsRequiringCustomFields(Type definitionType) => StepsRequiringCustomFields(definitionType);

    bool IWorkflow.RequiresCustomFieldsIn(Enum state, Type definitionType) =>
        state is TState typed
            ? RequiresCustomFieldsIn(typed, definitionType)
            : throw new ArgumentException($"{state.GetType().Name}.{state} is not a {typeof(TState).Name}.", nameof(state));

    public IReadOnlyList<Transition<TState, TEntity>> StepsRequiringCustomFields(Type definitionType)
    {
        ArgumentNullException.ThrowIfNull(definitionType);
        return [.. this.transitions.Where(transition => transition.Conditions.OfType<Conditions.IRequiredCustomFieldsCondition>().Any(condition => condition.DefinitionType == definitionType))];
    }

    public IReadOnlyList<Transition<TState, TEntity>> StepsRequiringCustomFieldsFrom(TState state, Type definitionType) =>
        [.. StepsRequiringCustomFields(definitionType).Where(transition => EqualityComparer<TState>.Default.Equals(transition.From, state)).OrderBy(transition => transition.SortOrder)];

    public bool RequiresCustomFieldsIn(TState state, Type definitionType) =>
        StepsRequiringCustomFields(definitionType).Any(transition => EqualityComparer<TState>.Default.Equals(transition.To, state));

    public IReadOnlyList<string> GetBlockingReasons(TEntity entity, string transitionKey, WorkflowEnvironment environment) =>
        BlockingReasons(entity, GetTransition(transitionKey), environment, new TransitionOutcome(), NewMovingSet());

    public bool CanTransition(TEntity entity, string transitionKey, WorkflowEnvironment environment) =>
        GetBlockingReasons(entity, transitionKey, environment).Count == 0;

    public WorkflowContext<TState, TEntity> CreateContext(TEntity entity, string transitionKey, WorkflowEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(environment);
        return new WorkflowContext<TState, TEntity>(entity, GetTransition(transitionKey), environment, comment: null, new TransitionOutcome());
    }

    public LockCandidates GetAllCandidatesForTransition(TEntity entity, string transitionKey, WorkflowEnvironment environment)
    {
        var candidates = new LockCandidates();
        AddCandidates(entity, GetTransition(transitionKey), environment, candidates);
        return candidates;
    }

    public async Task<TransitionOutcome> ExecuteTransitionAsync(TEntity entity, string transitionKey, WorkflowEnvironment environment, string? comment = null, CancellationToken cancellationToken = default)
    {
        var outcome = new TransitionOutcome();
        await ExecuteAsync(entity, GetTransition(transitionKey), environment, comment, outcome, cancellationToken);
        return outcome;
    }

    internal async Task ExecuteAsync(TEntity entity, Transition<TState, TEntity> transition, WorkflowEnvironment environment, string? comment, TransitionOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(environment);
        EnsureRegistered();

        if (outcome.Steps.Any(step => ReferenceEquals(step.Entity, entity)))
        {
            throw new TransitionRefusedException([CycleReason(entity)]);
        }

        var reasons = BlockingReasons(entity, transition, environment, outcome, NewMovingSet());
        if (reasons.Count > 0)
        {
            throw new TransitionRefusedException(reasons);
        }

        if (environment.StepAccess?.Invoke(entity, transition) is { } refusal)
        {
            throw new TransitionRefusedException([refusal]);
        }

        var context = new WorkflowContext<TState, TEntity>(entity, transition, environment, comment, outcome);
        outcome.Record(new TransitionStep(entity, transition.From, transition.To, transition.Key));

        foreach (var action in this[transition.From].LeaveActions)
        {
            await action.ExecuteAsync(context, cancellationToken);
        }

        foreach (var action in transition.Actions)
        {
            await action.ExecuteAsync(context, cancellationToken);
        }

        foreach (var action in this[transition.To].EnterActions)
        {
            await action.ExecuteAsync(context, cancellationToken);
        }

        entity.State = transition.To;
        entity.IsInTransition = false;
    }

    internal async Task CascadeInPlaceAsync(TEntity entity, WorkflowEnvironment environment, string? comment, TransitionOutcome outcome, CancellationToken cancellationToken)
    {
        EnsureRegistered();
        foreach (var cascade in StateCascades(entity.State))
        {
            await cascade.RunAsync(entity, environment, comment, outcome, cancellationToken);
        }
    }

    internal void AddInPlaceCandidates(TEntity entity, WorkflowEnvironment environment, LockCandidates candidates)
    {
        EnsureRegistered();
        if (!candidates.BeginMove(entity))
        {
            throw new TransitionRefusedException([CycleReason(entity)]);
        }

        try
        {
            foreach (var cascade in StateCascades(entity.State))
            {
                cascade.AddCandidates(entity, environment, candidates);
            }
        }
        finally
        {
            candidates.EndMove(entity);
        }
    }

    internal IReadOnlyList<string> InPlaceBlockingReasons(TEntity entity, WorkflowEnvironment environment, TransitionOutcome outcome, ISet<IStateEntity> moving)
    {
        EnsureRegistered();
        if (!moving.Add(entity))
        {
            return [CycleReason(entity)];
        }

        try
        {
            return [.. StateCascades(entity.State).SelectMany(cascade => cascade.BlockingReasons(entity, environment, outcome, moving))];
        }
        finally
        {
            moving.Remove(entity);
        }
    }

    internal void AddCandidates(TEntity entity, Transition<TState, TEntity> transition, WorkflowEnvironment environment, LockCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(environment);
        EnsureRegistered();
        if (!IsIn(entity, transition.From))
        {
            throw new TransitionRefusedException([WrongStateReason(entity, transition)]);
        }

        if (!candidates.BeginMove(entity))
        {
            throw new TransitionRefusedException([CycleReason(entity)]);
        }

        try
        {
            candidates.Add(entity);
            var context = new WorkflowContext<TState, TEntity>(entity, transition, environment, comment: null, new TransitionOutcome());
            foreach (var action in ActionsOf(transition))
            {
                action.AddLockCandidates(context, candidates);
            }
        }
        finally
        {
            candidates.EndMove(entity);
        }
    }

    internal IReadOnlyList<string> BlockingReasons(TEntity entity, Transition<TState, TEntity> transition, WorkflowEnvironment environment, TransitionOutcome outcome, ISet<IStateEntity> moving)
    {
        EnsureRegistered();
        if (!IsIn(entity, transition.From))
        {
            return [WrongStateReason(entity, transition)];
        }

        if (!moving.Add(entity))
        {
            return [CycleReason(entity)];
        }

        try
        {
            var context = new WorkflowContext<TState, TEntity>(entity, transition, environment, comment: null, outcome, [.. moving]);
            var reasons = transition.Conditions.SelectMany(condition => condition.GetBlockingReasons(context)).ToList();
            foreach (var check in ActionsOf(transition).OfType<IBlockingCheck<TState, TEntity>>())
            {
                reasons.AddRange(check.GetBlockingReasons(context, moving));
            }

            return reasons;
        }
        finally
        {
            moving.Remove(entity);
        }
    }

    internal void EnsureNotRegistered()
    {
        if (this.isRegistered)
        {
            throw new InvalidOperationException($"The {typeof(TEntity).Name} workflow is registered and can no longer change.");
        }
    }

    void IRegistrable.CompleteRegistration(WorkflowRegistry registry)
    {
        var entityName = typeof(TEntity).Name;

        var missing = Enum.GetValues<TState>().Where(state => !this.states.ContainsKey(state)).ToList();
        if (missing.Count > 0)
        {
            throw new WorkflowDefinitionException($"The {entityName} workflow does not register {string.Join(", ", missing)}; every {typeof(TState).Name} member needs AddState.");
        }

        foreach (var collection in CustomFieldCollection.Of(typeof(TEntity)))
        {
            var name = collection.Navigation.Name;
            foreach (var state in this.states.Values)
            {
                if (state.Fields.GetArea(name) != WorkflowAreas.Attributes || state.Fields.Overrides(name))
                {
                    throw new WorkflowDefinitionException($"{entityName}.{state.State} does not put {name} in the {WorkflowAreas.Attributes} area: every state configures .Area(WorkflowAreas.Attributes, flags, a => a.Field(e => e.{name})), with no access of its own for {name}.");
                }

                if (state.Fields.GetAreaAccess(WorkflowAreas.Attributes).HasFlag(FieldAccessFlags.Mandatory))
                {
                    throw new WorkflowDefinitionException($"{entityName}.{state.State} makes the {WorkflowAreas.Attributes} area Mandatory; a custom field is required by its own IsRequired, judged by RequiredAttributesCondition on the steps that need it.");
                }
            }
        }

        foreach (var transition in this.transitions)
        {
            if (transition.ConfirmationMessage is not null && transition.CommentPrompt is not null)
            {
                throw new WorkflowDefinitionException($"{entityName} transition '{transition.Key}' asks both a confirmation and a comment; a step asks one or the other.");
            }

            var unknown = transition.Conditions.SelectMany(c => c.Reads).Where(path => !MemberPaths.IsNavigationPath(typeof(TEntity), path)).ToList();
            if (unknown.Count > 0)
            {
                throw new WorkflowDefinitionException($"A condition on {entityName} transition '{transition.Key}' reads {string.Join(", ", unknown)}, which {entityName} does not have as a navigation (a reference or a collection of entities).");
            }

            foreach (var check in transition.Actions.OfType<IRegistrationCheck>())
            {
                check.CheckAtRegistration($"{entityName} transition '{transition.Key}'", registry);
            }

            if (transition.IsSave && SaveDesignationProblem(transition) is { } problem)
            {
                throw new WorkflowDefinitionException($"{entityName} transition '{transition.Key}' is marked IsSave, but {problem}; the save step a dialog runs on its own stores the edits and does nothing else.");
            }

            if (transition.CommentPrompt is not null && !typeof(Entities.Comments.ICommentedEntity).IsAssignableFrom(typeof(TEntity)))
            {
                throw new WorkflowDefinitionException($"{entityName} transition '{transition.Key}' asks a comment, but {entityName} has no comment threads (ICommentedEntity), so the answer would be lost.");
            }

            if (transition.CommentPrompt is not null && ((Enum)(object)transition.To).IsDeleteState())
            {
                throw new WorkflowDefinitionException($"{entityName} transition '{transition.Key}' asks a comment on a step into a delete state; the comment would outlive the record it explains.");
            }
        }

        if (this.transitions.Where(transition => transition.IsSave).GroupBy(transition => transition.From).FirstOrDefault(group => group.Count() > 1) is { } twice)
        {
            throw new WorkflowDefinitionException($"{entityName}.{twice.Key} marks {string.Join(" and ", twice.Select(transition => $"'{transition.Key}'"))} IsSave; a state has at most one save step.");
        }

        foreach (var state in this.states.Values)
        {
            foreach (var check in state.EnterActions.Concat(state.LeaveActions).OfType<IRegistrationCheck>())
            {
                check.CheckAtRegistration($"{entityName}.{state.State}", registry);
            }
        }

        this.isRegistered = true;
    }

    private string? SaveDesignationProblem(Transition<TState, TEntity> transition) =>
        !transition.IsSelfTransition ? $"it leads from {transition.From} to {transition.To}, and a save stays in its state"
        : transition.ConfirmationMessage is not null ? "it asks a confirmation"
        : transition.CommentPrompt is not null ? "it asks a comment"
        : transition.IsSystemDriven ? "it is system-driven"
        : transition.RequiresPlatformOwner ? "only a platform owner may take it"
        : transition.TakesNoEdits ? "it takes no edits"
        : ActionsOf(transition).Any() ? "it runs actions"
        : null;

    private static HashSet<IStateEntity> NewMovingSet() => new(ReferenceEqualityComparer.Instance);

    private IEnumerable<IAction<TState, TEntity>> ActionsOf(Transition<TState, TEntity> transition) =>
        this[transition.From].LeaveActions.Concat(transition.Actions).Concat(this[transition.To].EnterActions);

    private IEnumerable<IStateCascade<TState, TEntity>> StateCascades(TState state) => this[state].EnterActions.OfType<IStateCascade<TState, TEntity>>();

    private void EnsureRegistered()
    {
        if (!this.isRegistered)
        {
            throw new InvalidOperationException($"The {typeof(TEntity).Name} workflow runs only after a {nameof(WorkflowRegistry)} has registered it.");
        }
    }

    private static bool IsIn(TEntity entity, TState state) => EqualityComparer<TState>.Default.Equals(entity.State, state);

    private static string WrongStateReason(TEntity entity, Transition<TState, TEntity> transition) =>
        $"{typeof(TEntity).Name} is {entity.State}, but '{transition.Key}' leaves {transition.From}.";

    private static string CycleReason(TEntity entity) =>
        $"{typeof(TEntity).Name} {entity.Id} is reached again by a cascade of its own transition, so the cascade would never end.";
}

internal interface IRegistrable
{
    void CompleteRegistration(WorkflowRegistry registry);
}
