namespace Staffora.Core.Workflow;

public sealed class WorkflowDefinitionException : InvalidOperationException
{
    public WorkflowDefinitionException(string message) : base(message)
    {
    }
}

public sealed class TransitionRefusedException : InvalidOperationException
{
    public TransitionRefusedException(IReadOnlyList<string> reasons) : base($"The transition was refused: {string.Join(" ", reasons)}")
    {
        Reasons = reasons;
    }

    public IReadOnlyList<string> Reasons { get; }
}
