using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;

namespace Staffora.Core.Workflow;

public sealed class Transition<TState, TEntity> : ITransition
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly List<Condition<TState, TEntity>> conditions = [];
    private readonly List<IAction<TState, TEntity>> actions = [];

    public Transition(string key, TState from, TState to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        From = from;
        To = to;
    }

    public string Key { get; }

    public TState From { get; }

    public TState To { get; }

    public string? Label { get; init; }

    public string? Description { get; init; }

    public string? ConfirmationMessage { get; init; }

    public TransitionCommentPrompt? CommentPrompt { get; init; }

    public string? Icon { get; init; }

    public RolePermission? RequiredPermission { get; init; }

    public bool RequiresPlatformOwner { get; init; }

    public bool IsSystemDriven { get; init; }

    public bool ShowWhenBlocked { get; init; }

    public int SortOrder { get; init; }

    public TransitionPlacement Placement { get; init; }

    public bool IsSave { get; init; }

    public bool TakesNoEdits { get; init; }

    public bool NeverCascaded { get; init; }

    public bool IsSelfTransition => EqualityComparer<TState>.Default.Equals(From, To);

    public IReadOnlyList<Condition<TState, TEntity>> Conditions => this.conditions;

    public IReadOnlyList<IAction<TState, TEntity>> Actions => this.actions;

    public IReadOnlyList<string> ConditionDescriptions => [.. this.conditions.Select(condition => condition.Description)];


    internal Workflow<TState, TEntity>? Workflow { get; set; }

    Enum ITransition.From => From;

    Enum ITransition.To => To;

    public Transition<TState, TEntity> AddCondition(Condition<TState, TEntity> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        Workflow?.EnsureNotRegistered();
        this.conditions.Add(condition);
        return this;
    }

    public Transition<TState, TEntity> AddAction(IAction<TState, TEntity> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Workflow?.EnsureNotRegistered();
        this.actions.Add(action);
        return this;
    }
}
