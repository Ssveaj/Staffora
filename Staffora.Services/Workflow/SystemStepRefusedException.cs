namespace Staffora.Services.Workflow;

public sealed class SystemStepRefusedException : Exception
{
    public SystemStepRefusedException(string transitionKey, string entityType, IReadOnlyList<string> reasons) : base($"The step '{transitionKey}' on a {entityType} that this change calls for could not be taken, so nothing was changed. {string.Join(" ", reasons)}".TrimEnd())
    {
        TransitionKey = transitionKey;
        Reasons = reasons;
    }

    public string TransitionKey { get; }

    public IReadOnlyList<string> Reasons { get; }
}
