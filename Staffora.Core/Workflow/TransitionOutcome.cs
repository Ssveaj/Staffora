using Staffora.Core.Entities.Messaging;

namespace Staffora.Core.Workflow;

public sealed class TransitionOutcome
{
    internal List<OutgoingMessage> MessageQueue { get; } = [];

    internal List<Func<CancellationToken, Task>> AfterCommitQueue { get; } = [];

    internal List<Func<Microsoft.EntityFrameworkCore.DbContext, CancellationToken, Task>> BeforeSaveQueue { get; } = [];

    private readonly List<TransitionStep> steps = [];

    private readonly List<(IStateEntity Entity, Enum State)> reached = [];

    public IReadOnlyList<OutgoingMessage> OutgoingMessages => MessageQueue;

    public IReadOnlyList<Func<CancellationToken, Task>> AfterCommit => AfterCommitQueue;

    public IReadOnlyList<Func<Microsoft.EntityFrameworkCore.DbContext, CancellationToken, Task>> BeforeSave => BeforeSaveQueue;

    public IReadOnlyList<TransitionStep> Steps => this.steps;

    public IReadOnlyList<IStateEntity> LeftInDeleteState =>
        [.. this.reached.Where(reached => reached.State.IsDeleteState()).Select(reached => reached.Entity)];

    internal void Record(TransitionStep step)
    {
        this.steps.Add(step);
        this.reached.Add((step.Entity, step.To));
    }

    internal bool RecordAlreadyInDeleteState(IStateEntity entity, Enum state)
    {
        if (this.reached.Any(reached => ReferenceEquals(reached.Entity, entity)))
        {
            return false;
        }

        this.reached.Add((entity, state));
        return true;
    }
}

public sealed record TransitionStep(IStateEntity Entity, Enum From, Enum To, string TransitionKey);
