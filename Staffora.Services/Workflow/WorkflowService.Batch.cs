using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Coordination;
using Staffora.Services.Messaging;

namespace Staffora.Services.Workflow;

public sealed partial class WorkflowService
{
    public Task<Result<IReadOnlyList<Guid>>> CreateManyAndTransitionAsync<TState, TEntity>(IReadOnlyList<TEntity> entities, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionKey);
        return RunBatchAsync<IReadOnlyList<Guid>>($"the creates of {entities.Count} {typeof(TEntity).Name}", async (batch, token) =>
        {
            var ids = new List<Guid>();
            foreach (var entity in entities)
            {
                var created = await batch.CreateAsync<TState, TEntity>(entity, transitionKey, queryConfigurator, token);
                if (!created.IsSuccess)
                {
                    return Result<IReadOnlyList<Guid>>.Failure(created.Error);
                }

                ids.Add(created.Value);
            }

            return Result<IReadOnlyList<Guid>>.Success(ids);
        }, cancellationToken);
    }

    internal async Task<Result<T>> RunBatchAsync<T>(string what, Func<WorkflowBatch, CancellationToken, Task<Result<T>>> work, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(work);
        if (this.runtime.EffectiveUserId is null)
        {
            return Result<T>.Failure(NoUser);
        }

        StepBatch? batch = null;
        Result<T>? result = null;
        var committed = false;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            batch = new StepBatch { Context = context, Touched = TenantChangeCollector.Attach(context, this.runtime.TenantId) };
            using var lease = context.BeginPlatformScope(this.runtime, $"workflow steps of {what} in any tenant");
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            result = await work(new WorkflowBatch(this, batch), cancellationToken);
            if (!result.IsSuccess)
            {
                return result;
            }

            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
        }
        catch (Exception exception) when (committed || IsFailure(exception, cancellationToken))
        {
            if (!committed)
            {
                this.log.LogError(exception, "The batch of {What} failed; nothing of it was written.", what);
                return Result<T>.Failure($"The change ({what}) could not be completed, so nothing was changed.");
            }

            this.log.LogError(exception, "Cleaning up after the batch of {What} failed after its commit; the batch stands.", what);
        }

        await batch!.AfterCommit.RunAsync(this.log);
        PublishChanges(batch.Touched, what);
        return result!;
    }

    internal async Task<Result<Guid>> CreateInBatchAsync<TState, TEntity>(StepBatch batch, TEntity entity, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionKey);
        var created = new NewRecord<TEntity>(entity);
        var result = await RunAsync<TState, TEntity>(Guid.Empty, ByKey<TState, TEntity>(transitionKey), queryConfigurator, comment: null, cancellationToken, created: created, batch: batch);
        if (result.IsSuccess)
        {
            batch.Created.Add(created.Id);
        }

        return result.IsSuccess ? Result<Guid>.Success(created.Id, result.Message) : Result<Guid>.Failure(result.Error);
    }

    internal async Task<Result> TransitionInBatchAsync<TState, TEntity>(StepBatch batch, Guid entityId, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionKey);
        if (!batch.Created.Contains(entityId))
        {
            return Result.Failure(NotFound<TEntity>());
        }

        return await RunAsync<TState, TEntity>(entityId, ByKey<TState, TEntity>(transitionKey), queryConfigurator, comment: null, cancellationToken, batch: batch);
    }

    internal sealed class StepBatch
    {
        public required ApplicationDbContext Context { get; init; }

        public required TenantChangeCollector Touched { get; init; }

        public Guid? CorrelationId { get; set; }

        public HashSet<Guid> Created { get; } = [];

        public AfterCommitWork AfterCommit { get; } = new();

        public void Collect(TransitionOutcome outcome, Guid historyEntryId, IOutgoingMessageDispatcher dispatcher)
        {
            if (outcome.OutgoingMessages.Count > 0)
            {
                var notices = outcome.OutgoingMessages;
                AfterCommit.Add(token => dispatcher.DispatchAsync(notices, historyEntryId, token));
            }

            foreach (var work in outcome.AfterCommit)
            {
                AfterCommit.Add(work);
            }
        }
    }
}

public sealed class WorkflowBatch
{
    private readonly WorkflowService workflows;
    private readonly WorkflowService.StepBatch batch;

    internal WorkflowBatch(WorkflowService workflows, WorkflowService.StepBatch batch)
    {
        this.workflows = workflows;
        this.batch = batch;
    }

    public Task<Result<Guid>> CreateAsync<TState, TEntity>(TEntity entity, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        this.workflows.CreateInBatchAsync<TState, TEntity>(this.batch, entity, transitionKey, queryConfigurator, cancellationToken);

    public Task<Result> TransitionAsync<TState, TEntity>(Guid entityId, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        this.workflows.TransitionInBatchAsync<TState, TEntity>(this.batch, entityId, transitionKey, queryConfigurator, cancellationToken);
}
