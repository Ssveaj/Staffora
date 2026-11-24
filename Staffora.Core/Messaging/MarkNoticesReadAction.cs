using Staffora.Core.Entities;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Actions;

namespace Staffora.Core.Messaging;

public sealed class MarkNoticesReadAction<TState, TEntity> : IAction<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>, ITenantEntity
{
    public MarkNoticesReadAction(params IReadOnlyList<MessageType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Count == 0)
        {
            throw new ArgumentException("A step that marks notices read names the kinds of notice it settles.", nameof(types));
        }

        Types = [.. types.Distinct()];
    }

    public IReadOnlyList<MessageType> Types { get; }

    public Task ExecuteAsync(WorkflowContext<TState, TEntity> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var notices = context.ServiceProvider?.GetService(typeof(IHandledNotices)) as IHandledNotices
            ?? throw new InvalidOperationException($"The step marks the notices about a {typeof(TEntity).Name} read only where they can be marked.");
        var (tenantId, entityId) = (context.Entity.TenantId, context.Entity.Id);
        var readers = new List<Guid>();
        context.BeforeSave.Add(async (store, token) => readers.AddRange(await notices.MarkReadAsync(store, tenantId, typeof(TEntity), entityId, Types, token)));
        context.AfterCommit.Add(_ =>
        {
            notices.TellReaders(tenantId, readers);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public void AddLockCandidates(WorkflowContext<TState, TEntity> context, LockCandidates candidates)
    {
    }
}
