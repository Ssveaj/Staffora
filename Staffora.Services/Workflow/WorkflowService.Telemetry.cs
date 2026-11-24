using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Workflow;

public sealed partial class WorkflowService
{
    private async Task<Result> RunAsync<TState, TEntity>(Guid entityId, Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> pick, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator, string? comment, CancellationToken cancellationToken, NewRecord<TEntity>? created = null, DetachedEntityUpdater<TEntity>? edits = null, StepBatch? batch = null)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        using var measured = this.telemetry.StartWorkflowStep(typeof(TEntity).Name, entityId, stepKey: null, systemStep: false);
        var result = await RunStepAsync<TState, TEntity>(entityId, Measured(pick, measured), queryConfigurator, comment, cancellationToken, created, edits, batch);
        measured.Finish(result.IsSuccess, created?.Id ?? entityId);
        return result;
    }

    internal async Task RunSystemStepInTransactionAsync<TState, TEntity>(ApplicationDbContext context, TEntity entity, string transitionKey, CancellationToken cancellationToken, AfterCommitWork? afterCommit = null, Guid? correlationId = null)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        using var measured = this.telemetry.StartWorkflowStep(typeof(TEntity).Name, entity.Id, transitionKey, systemStep: true);
        try
        {
            await RunSystemStepCoreAsync<TState, TEntity>(context, entity, transitionKey, cancellationToken, afterCommit, correlationId);
            measured.Finish(succeeded: true);
        }
        catch (SystemStepRefusedException)
        {
            measured.Finish(succeeded: false);
            throw;
        }
    }

    private static Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> Measured<TState, TEntity>(Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> pick, WorkflowStepTelemetry measured)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        (entity, workflow) =>
        {
            var picked = pick(entity, workflow);
            measured.Picked(picked.Transition?.Key);
            return picked;
        };
}
