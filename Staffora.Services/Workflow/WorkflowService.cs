using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Coordination;
using Staffora.Services.Diagnostics;
using Staffora.Services.Messaging;

namespace Staffora.Services.Workflow;

public sealed partial class WorkflowService
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly IOutgoingMessageDispatcher dispatcher;
    private readonly TimeProvider clock;
    private readonly IServiceProvider services;
    private readonly IEnumerable<IDeleteCleanup> deleteCleanups;
    private readonly IEnumerable<IStepPreparation> preparations;
    private readonly IEnumerable<IDeleteBlocker> deleteBlockers;
    private readonly IEnumerable<IStepLock> stepLocks;
    private readonly TenantChangeBus changes;
    private readonly ILogger<WorkflowService> log;
    private readonly StafforaTelemetry telemetry;

    public WorkflowService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, IOutgoingMessageDispatcher dispatcher, TimeProvider clock, IServiceProvider services, IEnumerable<IDeleteCleanup> deleteCleanups, IEnumerable<IStepPreparation> preparations, IEnumerable<IDeleteBlocker> deleteBlockers, IEnumerable<IStepLock> stepLocks, TenantChangeBus changes, ILogger<WorkflowService> log, StafforaTelemetry telemetry)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.dispatcher = dispatcher;
        this.clock = clock;
        this.services = services;
        this.deleteCleanups = deleteCleanups;
        this.preparations = preparations;
        this.deleteBlockers = deleteBlockers;
        this.stepLocks = stepLocks;
        this.changes = changes;
        this.log = log;
        this.telemetry = telemetry;
    }

    public Task<Result> TransitionEntityAsync<TState, TEntity>(Guid entityId, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, string? comment = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionKey);
        return RunAsync<TState, TEntity>(entityId, ByKey<TState, TEntity>(transitionKey), queryConfigurator, comment, cancellationToken);
    }

    public Task<Result> TransitionEntityToStateAsync<TState, TEntity>(Guid entityId, TState targetState, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, string? comment = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        RunAsync<TState, TEntity>(
            entityId,
            (entity, workflow) => workflow.GetTransitions(entity.State, targetState).ToList() switch
            {
                [var only] => (only, null),
                [] => (null, $"The {typeof(TEntity).Name} is {entity.State} and has no step to {targetState}."),
                var several => (null, $"The {typeof(TEntity).Name} has {several.Count} steps from {entity.State} to {targetState}. Run one by its key."),
            },
            queryConfigurator,
            comment,
            cancellationToken);

    public Task<Result<Guid>> CreateAndTransitionAsync<TState, TEntity>(TEntity entity, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, string? comment = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        CreateWithAfterInsertAsync<TState, TEntity>(entity, transitionKey, queryConfigurator, comment, afterInsert: null, cancellationToken);

    internal Task<Result<Guid>> CreateAndTransitionAsync<TState, TEntity>(TEntity entity, string transitionKey, Func<ApplicationDbContext, TEntity, CancellationToken, Task> afterInsert, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, string? comment = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentNullException.ThrowIfNull(afterInsert);
        return CreateWithAfterInsertAsync<TState, TEntity>(entity, transitionKey, queryConfigurator, comment, afterInsert, cancellationToken);
    }

    private async Task<Result<Guid>> CreateWithAfterInsertAsync<TState, TEntity>(TEntity entity, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator, string? comment, Func<ApplicationDbContext, TEntity, CancellationToken, Task>? afterInsert, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionKey);
        var created = new NewRecord<TEntity>(entity) { AfterInsert = afterInsert };
        var result = await RunAsync<TState, TEntity>(Guid.Empty, ByKey<TState, TEntity>(transitionKey), queryConfigurator, comment, cancellationToken, created: created);
        return result.IsSuccess ? Result<Guid>.Success(created.Id, result.Message) : Result<Guid>.Failure(result.Error);
    }

    public Task<Result> SaveAndTransitionAsync<TState, TEntity>(DetachedEntityUpdater<TEntity> updater, string transitionKey, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, string? comment = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionKey);

        var entityId = (Guid)updater.OriginalValue(nameof(IStateEntity.Id))!;
        return RunAsync<TState, TEntity>(entityId, ByKey<TState, TEntity>(transitionKey), queryConfigurator, comment, cancellationToken, edits: updater);
    }

    private static Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> ByKey<TState, TEntity>(string transitionKey)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        (_, workflow) => workflow.TryGetTransition(transitionKey, out var transition)
            ? (transition, null)
            : (null, $"The {typeof(TEntity).Name} has no step '{transitionKey}'.");

    public async Task<Result> SaveEntityAsync<TEntity>(DetachedEntityUpdater<TEntity> updater, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(updater);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var touched = TenantChangeCollector.Attach(context, this.runtime.TenantId);
            var saved = await updater.SaveAsync(context, cancellationToken);
            if (saved.IsSuccess)
            {
                await touched.NameAffectedAsync(context, this.log, CancellationToken.None);
                PublishChanges(touched, $"the save of {typeof(TEntity).Name}");
            }

            return saved;
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            this.log.LogError(exception, "Saving {EntityType} {EntityId} failed.", typeof(TEntity).Name, (updater.Entity as IStateEntity)?.Id);
            return Result.Failure($"The {typeof(TEntity).Name} could not be saved, so nothing was changed. Reload it and try again.");
        }
    }

    private async Task<Result> RunStepAsync<TState, TEntity>(Guid entityId, Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> pick, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator, string? comment, CancellationToken cancellationToken, NewRecord<TEntity>? created = null, DetachedEntityUpdater<TEntity>? edits = null, StepBatch? batch = null)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        if (this.runtime.EffectiveUserId is null)
        {
            return Result.Failure(NoUser);
        }

        pick = UserStepsOnly(pick);
        Transition<TState, TEntity>? transition = null;
        TransitionOutcome? committed = null;
        var historyEntryId = Guid.Empty;

        IDbContextTransaction? opened = null;

        Transition<TState, TEntity>? editAuthority = null;

        TenantChangeCollector? touched = null;

        var editedPrepared = false;

        var followUpWork = batch?.AfterCommit ?? new AfterCommitWork();
        try
        {
            if (edits is not null)
            {
                (transition, var stepRefusal) = await RefuseStepWithEditsAsync(edits, pick, cancellationToken);
                if (stepRefusal is not null)
                {
                    return Result.Failure(stepRefusal);
                }
            }

            await using var ownContext = batch is null ? this.dbFactory.CreateDbContext(this.runtime) : null;
            var context = batch?.Context ?? ownContext!;
            if (batch is not null)
            {
                context.ChangeTracker.Clear();
            }

            touched = batch?.Touched ?? TenantChangeCollector.Attach(context, this.runtime.TenantId);

            using var lease = batch is null ? context.BeginPlatformScope(this.runtime, PlatformReason<TEntity>()) : null;

            if (created is not null)
            {
                opened = await BeginLockedAsync<TEntity>(context, entityId: null, cancellationToken, inBatch: batch is not null);
                var row = CopyForInsert(context, created.Source);
                (transition, var createRefusal) = await RefuseCreateAsync(context, row, pick, cancellationToken);
                if (createRefusal is not null)
                {
                    return Result.Failure(createRefusal);
                }

                if (batch is null)
                {
                    opened ??= await context.Database.BeginTransactionAsync(cancellationToken);
                }

                if (await HeldTenantRefusalInTransactionAsync(context, TenantOf(row) is { } owner && owner != Guid.Empty ? owner : this.runtime.Current.TenantId, cancellationToken) is { } heldCreate)
                {
                    return Result.Failure(heldCreate);
                }

                StampWritesOnOtherTenantsBehalf(context);
                await context.SaveChangesAsync(cancellationToken);
                entityId = created.Id = row.Id;

                if (created.AfterInsert is { } afterInsert)
                {
                    await afterInsert(context, row, cancellationToken);
                }
            }

            if (edits is not null)
            {
                opened = await BeginLockedAsync<TEntity>(context, entityId, cancellationToken) ?? await context.Database.BeginTransactionAsync(cancellationToken);

                if (await HeldTenantRefusalInTransactionAsync(context, TenantOf(edits.Entity), cancellationToken) is { } heldEdits)
                {
                    return Result.Failure(heldEdits);
                }

                await PrepareAsync<TEntity>(context, entityId, cancellationToken);

                if (transition!.TakesNoEdits)
                {
                    if (edits.HasChanges())
                    {
                        this.log.LogInformation("Refused {TransitionKey} on {EntityType} {EntityId} for user {UserId}: edits reached it while it waited, and it takes none.", transition.Key, typeof(TEntity).Name, entityId, this.runtime.EffectiveUserId);
                        return Result.Failure(TransitionRefusals.PendingEdits);
                    }
                }
                else
                {
                    if (edits.HasChanges())
                    {
                        var editsWorkflow = WorkflowRegistry.Default.Get<TState, TEntity>();
                        if (await EditAuthorityRefusalAsync(editsWorkflow, transition, cancellationToken) is { } notYours)
                        {
                            this.log.LogInformation("Refused {TransitionKey} with edits on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, entityId, this.runtime.EffectiveUserId, notYours);
                            return Result.Failure(notYours);
                        }

                        editAuthority = transition.IsSave ? null : editsWorkflow.GetEditAuthority(transition.From);
                    }

                    var saved = await edits.SaveInTransactionAsync(context, _ => StampWritesOnOtherTenantsBehalf(context), cancellationToken);
                    if (!saved.IsSuccess)
                    {
                        return Result.Failure(string.IsNullOrWhiteSpace(saved.Error) ? $"The {typeof(TEntity).Name} could not be saved, so nothing was changed." : saved.Error);
                    }
                }

                context.ChangeTracker.Clear();

                if (!transition!.TakesNoEdits)
                {
                    editedPrepared = true;
                    await PrepareEditedAsync<TEntity>(context, entityId, cancellationToken);
                }
            }

            if (created is null && edits is null)
            {
                opened = await BeginLockedAsync<TEntity>(context, entityId, cancellationToken, inBatch: batch is not null);
            }

            var entity = await LoadAsync(context, entityId, queryConfigurator, cancellationToken);
            if (entity is null)
            {
                return Result.Failure(NotFound<TEntity>());
            }

            if (IsUntracked(context, entity))
            {
                this.log.LogWarning("Refused a step on {EntityType} {EntityId}: it was loaded without change tracking.", typeof(TEntity).Name, entityId);
                return Result.Failure(Untracked<TEntity>());
            }

            if (edits is null && created is null)
            {
                await PrepareAsync<TEntity>(context, entityId, cancellationToken);
            }

            var workflow = WorkflowRegistry.Default.Get<TState, TEntity>();
            (transition, var error) = pick(entity, workflow);
            if (transition is null)
            {
                return Result.Failure(error);
            }

            if (!EqualityComparer<TState>.Default.Equals(entity.State, transition.From))
            {
                return Result.Failure($"The {typeof(TEntity).Name} is {entity.State}, but '{transition.Key}' leaves {transition.From}. Reload it and try again.");
            }

            if (await AccessRefusalAsync(transition, cancellationToken) is { } refusal)
            {
                this.log.LogInformation("Refused {TransitionKey} on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, entityId, this.runtime.EffectiveUserId, refusal);
                return Result.Failure(refusal);
            }

            if (await HeldTenantRefusalAsync(context, TenantOf(entity), cancellationToken) is { } held)
            {
                this.log.LogInformation("Refused {TransitionKey} on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, entityId, this.runtime.EffectiveUserId, held);
                return Result.Failure(held);
            }

            if (CommentRefusal(transition, comment) is { } commentRefusal)
            {
                return Result.Failure(commentRefusal);
            }

            var cascaded = new List<CascadedTransition>();
            await LoadCascadesAsync(context, entity, workflow, transition.Key, cascaded, cancellationToken);

            if (await CascadedAccessRefusalAsync(transition, cascaded, cancellationToken) is { } cascadeRefusal)
            {
                this.log.LogInformation("Refused {TransitionKey} on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, entityId, this.runtime.EffectiveUserId, cascadeRefusal);
                return Result.Failure(cascadeRefusal);
            }

            var approved = new ApprovedSteps();
            approved.Add(entity, transition);
            foreach (var step in cascaded)
            {
                approved.Add(step.Entity, step.Transition);
            }

            var environment = EnvironmentFor(context, StepAccess(context, approved, transition), await CustomFieldsAsync(context, workflow, entity, cancellationToken));
            var reasons = workflow.GetBlockingReasons(entity, transition.Key, environment);

            if (editAuthority is not null && !ReferenceEquals(editAuthority, transition))
            {
                reasons = [.. reasons.Concat(workflow.GetBlockingReasons(entity, editAuthority.Key, environment)).Distinct(StringComparer.Ordinal)];
            }

            if (reasons.Count > 0)
            {
                return Result.Failure(string.Join(" ", reasons));
            }

            await using var transaction = batch is not null ? null : opened ?? await context.Database.BeginTransactionAsync(cancellationToken);
            opened = null;

            if (await HeldTenantRefusalInTransactionAsync(context, TenantOf(entity), cancellationToken) is { } heldNow)
            {
                this.log.LogInformation("Refused {TransitionKey} on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, entityId, this.runtime.EffectiveUserId, heldNow);
                return Result.Failure(heldNow);
            }

            var candidates = workflow.GetAllCandidatesForTransition(entity, transition.Key, environment);
            if (candidates.All.FirstOrDefault(candidate => IsUntracked(context, candidate)) is { } untracked)
            {
                this.log.LogWarning("Refused {TransitionKey} on {EntityType} {EntityId}: it names a {CandidateType} the context does not track.", transition.Key, typeof(TEntity).Name, entityId, untracked.GetType().Name);
                return Result.Failure($"The step '{transition.Key}' names a {untracked.GetType().Name} that was not loaded with change tracking, so nothing was changed.");
            }

            if (candidates.All.FirstOrDefault(candidate => context.Entry(candidate).State == EntityState.Added) is { } added)
            {
                this.log.LogWarning("Refused {TransitionKey} on {EntityType} {EntityId}: it names a new {CandidateType} as a record it changes.", transition.Key, typeof(TEntity).Name, entityId, ModelTypeOf(context, added).Name);
                return Result.Failure($"The step '{transition.Key}' names a new {ModelTypeOf(context, added).Name} as a record it changes, but a record that is not saved yet cannot be locked, so nothing was changed.");
            }

            await LockAsync(context, candidates, cancellationToken);

            if (edits is not null && transition.TakesNoEdits && await ChangedSinceOpenedAsync(context, entity, edits, transition, cancellationToken) is { } stale)
            {
                this.log.LogInformation("Refused {TransitionKey} on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, entityId, this.runtime.EffectiveUserId, stale);
                return Result.Failure(stale);
            }

            if (await DeleteBlockerReasonsAsync(context, entity, transition, cascaded, cancellationToken) is { Count: > 0 } blocked)
            {
                this.log.LogInformation("Refused {TransitionKey} on {EntityType} {EntityId}: a delete blocker reports use. {Reasons}", transition.Key, typeof(TEntity).Name, entityId, string.Join(" ", blocked));
                return Result.Failure(string.Join(" ", blocked));
            }

            var outcome = await workflow.ExecuteTransitionAsync(entity, transition.Key, environment, comment, cancellationToken);

            foreach (var write in outcome.BeforeSave)
            {
                await write(context, cancellationToken);
            }

            RefuseStateWrittenOutsideTheEngine(context, outcome, candidates);

            if (transition.CommentPrompt is { } prompt && entity is ICommentedEntity commented && !string.IsNullOrWhiteSpace(comment))
            {
                await CommentThreadWriter.AddAsync(context, commented, prompt.Audience, CommentKind.TransitionNote, CommentAuthorType.User, comment, transition.Key, cancellationToken);
            }

            var entries = StateHistoryWriter.Add(context, outcome, batch?.CorrelationId);
            historyEntryId = entries[0].Id;
            var correlationId = entries[0].CorrelationId;
            if (batch is not null)
            {
                batch.CorrelationId ??= correlationId;
            }

            OutgoingMessageWriter.Add(context, outcome, historyEntryId);

            foreach (var locked in candidates.All)
            {
                locked.IsInTransition = false;
            }

            await RemoveDeletedAsync(context, outcome, cancellationToken);

            StampWritesOnOtherTenantsBehalf(context);

            using (context.BeginTransitionSaveScope(candidates.All, transition.Key))
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            await RunFollowUpsAsync(context, entity, new StepFollowUpContext(outcome, correlationId, followUpWork), cancellationToken);

            await touched.NameAffectedAsync(context, this.log, CancellationToken.None);

            if (batch is not null)
            {
                batch.Collect(outcome, historyEntryId, this.dispatcher);
                return Result.Success(message: $"The {typeof(TEntity).Name} is {transition.To}.");
            }

            await transaction!.CommitAsync(CancellationToken.None);
            committed = outcome;

            edits?.AcceptChanges();
        }
        catch (Exception exception) when (committed is not null || IsFailure(exception, cancellationToken))
        {
            if (committed is null)
            {
                return Refused<TEntity>(exception, entityId, transition);
            }

            this.log.LogError(exception, "Cleaning up after {TransitionKey} on {EntityType} {EntityId} failed after its commit; the step stands.", transition!.Key, typeof(TEntity).Name, entityId);
        }
        finally
        {
            if (opened is not null)
            {
                await opened.DisposeAsync();
            }

            if (editedPrepared)
            {
                ForgetEdited<TEntity>(entityId);
            }
        }

        await AfterCommitAsync(committed!, historyEntryId, transition!.Key);
        await followUpWork.RunAsync(this.log);
        PublishChanges(touched, transition.Key);
        return Result.Success(message: $"The {typeof(TEntity).Name} is {transition.To}.");
    }

    private static Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> UserStepsOnly<TState, TEntity>(Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> pick)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState> =>
        (entity, workflow) => pick(entity, workflow) switch
        {
            ({ IsSystemDriven: true } systemStep, _) => (null, SystemDrivenRefusal(NameOf(systemStep))),
            var picked => picked,
        };

    private async Task RunFollowUpsAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        var entityType = ModelTypeOf(context, entity);
        foreach (var followUp in this.services.GetServices<IStepFollowUp>().Where(followUp => followUp.EntityType.IsAssignableFrom(entityType)))
        {
            await followUp.RunAsync(context, entity, taken, cancellationToken);
        }
    }

    private async Task RunSystemStepCoreAsync<TState, TEntity>(ApplicationDbContext context, TEntity entity, string transitionKey, CancellationToken cancellationToken, AfterCommitWork? afterCommit, Guid? correlationId)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A system step inside another write runs in that write's transaction: begin one first.");
        }

        if (context.Entry(entity).State is EntityState.Detached or EntityState.Added)
        {
            throw new InvalidOperationException($"A system step runs on a stored {typeof(TEntity).Name} the caller's context tracks.");
        }

        var workflow = WorkflowRegistry.Default.Get<TState, TEntity>();
        if (!workflow.TryGetTransition(transitionKey, out var transition))
        {
            throw new InvalidOperationException($"The {typeof(TEntity).Name} has no step '{transitionKey}'.");
        }

        if (SystemStepProblem(workflow, transition, carriesAfterCommit: afterCommit is not null) is { } problem)
        {
            throw new InvalidOperationException(afterCommit is null
                ? $"Only a system-driven step without cascades or a delete runs inside another write; '{transition.Key}' {problem}."
                : $"Only a system-driven step without a delete runs inside another write whose after-commit work is run; '{transition.Key}' {problem}.");
        }

        if (!EqualityComparer<TState>.Default.Equals(entity.State, transition.From))
        {
            throw new SystemStepRefusedException(transition.Key, typeof(TEntity).Name, [$"The {typeof(TEntity).Name} is {entity.State}, but '{transition.Key}' leaves {transition.From}."]);
        }

        foreach (var stepLock in this.stepLocks.Where(stepLock => stepLock.EntityType.IsAssignableFrom(ModelTypeOf(context, entity))))
        {
            await stepLock.TakeAsync(context, entity.Id, cancellationToken);
        }

        if (await HeldTenantRefusalInTransactionAsync(context, TenantOf(entity), cancellationToken) is { } held)
        {
            throw new SystemStepRefusedException(transition.Key, typeof(TEntity).Name, [held]);
        }

        await PrepareAsync<TEntity>(context, entity.Id, cancellationToken);

        var cascaded = new List<CascadedTransition>();
        await LoadCascadesAsync(context, entity, workflow, transition.Key, cascaded, cancellationToken);
        var approved = new ApprovedSteps();
        approved.Add(entity, transition);
        foreach (var step in cascaded)
        {
            approved.Add(step.Entity, step.Transition);
        }

        var environment = EnvironmentFor(context, StepAccess(context, approved, transition), await CustomFieldsAsync(context, workflow, entity, cancellationToken));
        if (workflow.GetBlockingReasons(entity, transition.Key, environment) is { Count: > 0 } reasons)
        {
            throw new SystemStepRefusedException(transition.Key, typeof(TEntity).Name, reasons);
        }

        var candidates = workflow.GetAllCandidatesForTransition(entity, transition.Key, environment);
        if (candidates.All.FirstOrDefault(candidate => context.Entry(candidate).State is EntityState.Detached or EntityState.Added) is { } unsaved)
        {
            throw new InvalidOperationException($"The step '{transition.Key}' names a {unsaved.GetType().Name} that is not a stored row the context tracks.");
        }

        await LockAsync(context, candidates, cancellationToken);
        var outcome = await workflow.ExecuteTransitionAsync(entity, transition.Key, environment, comment: null, cancellationToken);
        if (outcome.LeftInDeleteState.Count > 0 || (afterCommit is null && (outcome.OutgoingMessages.Count > 0 || outcome.AfterCommit.Count > 0)))
        {
            throw new InvalidOperationException($"The step '{transition.Key}' queues notices, after-commit work or a delete, which a step inside another write carries only when its caller runs its after-commit work, and never a delete: its caller commits.");
        }

        foreach (var write in outcome.BeforeSave)
        {
            await write(context, cancellationToken);
        }

        RefuseStateWrittenOutsideTheEngine(context, outcome, candidates);
        var historyEntryId = StateHistoryWriter.Add(context, outcome, correlationId)[0].Id;

        var notices = OutgoingMessageWriter.Add(context, outcome, historyEntryId);
        foreach (var locked in candidates.All)
        {
            locked.IsInTransition = false;
        }

        StampWritesOnOtherTenantsBehalf(context);
        using (context.BeginTransitionSaveScope(candidates.All, transition.Key))
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        if (afterCommit is not null)
        {
            if (notices.Count > 0)
            {
                afterCommit.Add(token => this.dispatcher.DispatchAsync(notices, historyEntryId, token));
            }

            foreach (var work in outcome.AfterCommit)
            {
                afterCommit.Add(work);
            }
        }

        this.log.LogInformation("{TransitionKey} moved {EntityType} {EntityId} from {From} to {To} inside another write.", transition.Key, typeof(TEntity).Name, entity.Id, transition.From, transition.To);
    }

    internal static string? SystemStepProblem(IWorkflow workflow, ITransition transition, bool carriesAfterCommit = false)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(transition);
        return !transition.IsSystemDriven ? "is a user's step, not a system-driven one"
            : !carriesAfterCommit && workflow.GetCascades(transition.Key).Count > 0 ? "takes child records along"
            : transition.To.IsDeleteState() ? "leads into a delete state"
            : null;
    }

    private static void StampWritesOnOtherTenantsBehalf(ApplicationDbContext context) => context.StampOnOtherTenantsBehalf();

    private static string PlatformReason<TEntity>() => $"workflow steps of a {typeof(TEntity).Name} in any tenant";

    private async Task AfterCommitAsync(TransitionOutcome outcome, Guid historyEntryId, string transitionKey)
    {
        if (outcome.OutgoingMessages.Count > 0)
        {
            try
            {
                await this.dispatcher.DispatchAsync(outcome.OutgoingMessages, historyEntryId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, "Dispatching the messages of {TransitionKey} (history entry {HistoryEntryId}) failed after its commit.", transitionKey, historyEntryId);
            }
        }

        foreach (var work in outcome.AfterCommit)
        {
            try
            {
                await work(CancellationToken.None);
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, "After-commit work of {TransitionKey} (history entry {HistoryEntryId}) failed; the transition stands.", transitionKey, historyEntryId);
            }
        }
    }

    private void PublishChanges(TenantChangeCollector? touched, string transitionKey)
    {
        try
        {
            touched?.PublishTo(this.changes);
        }
        catch (Exception exception)
        {
            this.log.LogError(exception, "Publishing the changes of {TransitionKey} failed after its commit; the step stands, and open pages read again when they next load.", transitionKey);
        }
    }

    private static Task<TEntity?> LoadAsync<TEntity>(ApplicationDbContext context, Guid entityId, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator, CancellationToken cancellationToken)
        where TEntity : class
    {
        IQueryable<TEntity> query = context.Set<TEntity>();
        if (queryConfigurator is not null)
        {
            query = queryConfigurator(query);
        }

        return query.AsTracking().FirstOrDefaultAsync(entity => EF.Property<Guid>(entity, nameof(IStateEntity.Id)) == entityId, cancellationToken);
    }

    private static bool IsUntracked(ApplicationDbContext context, object entity) => context.Entry(entity).State == EntityState.Detached;
}
