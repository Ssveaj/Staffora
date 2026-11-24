using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow;

public interface IWorkflow
{
    Type EntityType { get; }

    Type StateType { get; }

    IReadOnlyList<Enum> States { get; }

    IReadOnlyList<ITransition> Transitions { get; }

    FieldAccessFlags GetFieldAccess(Enum state, string field);

    bool DeclaresField(string field);

    IReadOnlyList<ICascadeAction> GetCascades(string transitionKey);

    IReadOnlyList<ICascadeAction> GetStateCascades(Enum state);

    ITransition? GetSaveStep(Enum state);

    ITransition? GetEditAuthority(Enum state);

    bool EditsGoWithItsSteps(Enum state);

    IReadOnlyList<ITransition> StepsRequiringCustomFields(Type definitionType);

    bool RequiresCustomFieldsIn(Enum state, Type definitionType);
}

public interface ITransition
{
    string Key { get; }

    Enum From { get; }

    Enum To { get; }

    string? Label { get; }

    string? Description { get; }

    string? ConfirmationMessage { get; }

    TransitionCommentPrompt? CommentPrompt { get; }

    string? Icon { get; }

    RolePermission? RequiredPermission { get; }

    bool RequiresPlatformOwner { get; }

    bool IsSystemDriven { get; }

    bool ShowWhenBlocked { get; }

    int SortOrder { get; }

    TransitionPlacement Placement { get; }

    bool IsSave { get; }

    bool TakesNoEdits { get; }

    bool NeverCascaded { get; }

    IReadOnlyList<string> ConditionDescriptions { get; }
}

public enum TransitionPlacement
{
    Trailing = 0,

    Leading = 1,
}
