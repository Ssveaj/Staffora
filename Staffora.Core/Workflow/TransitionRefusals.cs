namespace Staffora.Core.Workflow;

public static class TransitionRefusals
{
    public const string PendingEdits = "Save or discard your changes first: this step takes the record as it is stored";

    public static string ChangedSinceOpened(string entityName, string stepName) =>
        $"The {entityName} was changed after you opened it, so '{stepName}' was not taken. Reopen it to see the changes, then try again.";

    public const string EditsNotYours = "Discard your changes first: you may take this step, but not save the changes that would go with it";

    public static string EditsRequireSaveAccess(string entityName, string stepName, string saveRefusal) =>
        $"'{stepName}' would also store your changes to the {entityName}, and you may not save them: {saveRefusal} Discard your changes, then take the step again.";

    public static string NoEditsInState(string entityName, string state, string stepName) =>
        $"The {entityName} takes no changes while it is {state}, so '{stepName}' runs without them. Discard your changes, then take the step again.";

    public const string NoEditsHere = "Discard your changes first: nothing is saved in this state";
}
