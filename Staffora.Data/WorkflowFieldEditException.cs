namespace Staffora.Data;

public sealed class WorkflowFieldEditException : InvalidOperationException
{
    public WorkflowFieldEditException(string entityType, string key, Enum? persistedState, IReadOnlyList<string> fields, string message, bool heldByTransition = false, IReadOnlyList<string>? emptiedRequiredFields = null, IReadOnlyList<string>? emptiedCustomFields = null) : base(message)
    {
        EntityType = entityType;
        Key = key;
        PersistedState = persistedState;
        Fields = fields;
        HeldByTransition = heldByTransition;
        EmptiedRequiredFields = emptiedRequiredFields ?? [];
        EmptiedCustomFields = emptiedCustomFields ?? [];
    }

    public string EntityType { get; }

    public string Key { get; }

    public Enum? PersistedState { get; }

    public IReadOnlyList<string> Fields { get; }

    public bool HeldByTransition { get; }

    public IReadOnlyList<string> EmptiedRequiredFields { get; }

    public IReadOnlyList<string> EmptiedCustomFields { get; }
}
