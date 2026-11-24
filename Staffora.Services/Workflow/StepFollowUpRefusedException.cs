namespace Staffora.Services.Workflow;

public sealed class StepFollowUpRefusedException : Exception
{
    public StepFollowUpRefusedException(string reason, Exception? inner = null) : base(reason, inner)
    {
    }
}
