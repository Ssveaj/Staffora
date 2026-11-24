using Staffora.Core.Workflow;
using Staffora.Data;

namespace Staffora.Services.Workflow;

public interface IStepFollowUp
{
    Type EntityType { get; }

    Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken);
}

public sealed record StepFollowUpContext(TransitionOutcome Outcome, Guid CorrelationId, AfterCommitWork AfterCommit);
