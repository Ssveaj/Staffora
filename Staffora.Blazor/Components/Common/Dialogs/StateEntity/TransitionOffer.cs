using Staffora.Core.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public sealed record TransitionOffer(ITransition Transition, bool CanRun, IReadOnlyList<string> BlockingReasons)
{
    public bool WaitsForSave { get; init; }
}

public sealed record TransitionAttempt(bool Ran, string? Error)
{
    public static TransitionAttempt Succeeded { get; } = new(true, null);

    public static TransitionAttempt GivenUp { get; } = new(false, null);

    public static TransitionAttempt Refused(string error) => new(false, error);
}
