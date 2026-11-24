using Staffora.Core.Entities.Messaging;

namespace Staffora.Core.Workflow;

public sealed class WorkflowContext<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly IReadOnlyCollection<IStateEntity>? judgedWith;

    internal WorkflowContext(TEntity entity, Transition<TState, TEntity> transition, WorkflowEnvironment environment, string? comment, TransitionOutcome outcome, IReadOnlyCollection<IStateEntity>? judgedWith = null)
    {
        Entity = entity;
        Transition = transition;
        Environment = environment;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        Outcome = outcome;
        this.judgedWith = judgedWith;
    }

    public TEntity Entity { get; }

    public Transition<TState, TEntity> Transition { get; }

    public WorkflowEnvironment Environment { get; }

    public TState TargetState => Transition.To;

    public string? Comment { get; }

    public IList<OutgoingMessage> OutgoingMessages => Outcome.MessageQueue;

    public IList<Func<CancellationToken, Task>> AfterCommit => Outcome.AfterCommitQueue;

    public IList<Func<Microsoft.EntityFrameworkCore.DbContext, CancellationToken, Task>> BeforeSave => Outcome.BeforeSaveQueue;

    public TimeProvider TimeProvider => Environment.TimeProvider;

    public IServiceProvider? ServiceProvider => Environment.ServiceProvider;

    internal TransitionOutcome Outcome { get; }

    public bool IsTakenWith(IStateEntity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return !ReferenceEquals(other, Entity)
            && ((this.judgedWith?.Any(entity => ReferenceEquals(entity, other)) ?? false) || Outcome.Steps.Any(step => ReferenceEquals(step.Entity, other)));
    }

    public bool IsNavigationLoaded(string path) => IsNavigationLoaded(Entity, Environment, path);

    internal static bool IsNavigationLoaded(object entity, WorkflowEnvironment environment, string path)
    {
        IEnumerable<object> current = [entity];
        foreach (var segment in path.Split('.'))
        {
            var next = new List<object>();
            foreach (var item in current)
            {
                if (!environment.NavigationLoadState.IsLoaded(item, segment))
                {
                    return false;
                }

                next.AddRange(MemberPaths.Values(item.GetType().GetProperty(segment)?.GetValue(item)));
            }

            current = next;
        }

        return true;
    }
}
