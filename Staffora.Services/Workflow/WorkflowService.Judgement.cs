using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Services.Workflow;

public sealed partial class WorkflowService
{
    public async Task<Result<IReadOnlyList<TransitionAvailability<TState, TEntity>>>> GetTransitionAccessAsync<TState, TEntity>(TState state, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        if (this.runtime.EffectiveUserId is null)
        {
            return Result<IReadOnlyList<TransitionAvailability<TState, TEntity>>>.Failure(NoUser);
        }

        try
        {
            var answers = new List<TransitionAvailability<TState, TEntity>>();
            var outsideATenant = CreateOutsideATenantRefusal<TEntity>();
            var held = outsideATenant is null ? await HeldTenantRefusalForCreateAsync<TEntity>(cancellationToken) : null;
            foreach (var transition in WorkflowRegistry.Default.Get<TState, TEntity>().GetTransitionsFrom(state))
            {
                answers.Add(new TransitionAvailability<TState, TEntity>(transition, outsideATenant ?? await ListedAccessRefusalAsync(transition, cancellationToken) ?? held, []));
            }

            return Result<IReadOnlyList<TransitionAvailability<TState, TEntity>>>.Success(answers);
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            this.log.LogError(exception, "Reading the steps of a new {EntityType} failed.", typeof(TEntity).Name);
            return Result<IReadOnlyList<TransitionAvailability<TState, TEntity>>>.Failure($"The steps of the {typeof(TEntity).Name} could not be read. Try again.");
        }
    }

    public Task<Result<IReadOnlyList<TransitionAvailability<TState, TEntity>>>> GetAvailableTransitionsAsync<TState, TEntity>(Guid entityId, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator = null, CancellationToken cancellationToken = default)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        return JudgeStoredEntityAsync<TState, TEntity, IReadOnlyList<TransitionAvailability<TState, TEntity>>>(entityId, queryConfigurator, async (context, workflow, entity, environment, held) =>
        {
            var answers = new List<TransitionAvailability<TState, TEntity>>();
            foreach (var transition in workflow.GetTransitionsFrom(entity.State))
            {
                var (refusal, reasons) = await JudgeTransitionAsync(context, workflow, entity, transition, environment, held, cancellationToken);
                answers.Add(new TransitionAvailability<TState, TEntity>(transition, transition.IsSystemDriven ? await ListedAccessRefusalAsync(transition, cancellationToken) : refusal, reasons));
            }

            return answers;
        }, cancellationToken);
    }

    private async Task<Result<TResult>> JudgeStoredEntityAsync<TState, TEntity, TResult>(Guid entityId, Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator, Func<ApplicationDbContext, Workflow<TState, TEntity>, TEntity, WorkflowEnvironment, string?, Task<TResult>> judge, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        if (this.runtime.EffectiveUserId is null)
        {
            return Result<TResult>.Failure(NoUser);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, PlatformReason<TEntity>());
            var entity = await LoadAsync(context, entityId, queryConfigurator, cancellationToken);
            if (entity is null)
            {
                return Result<TResult>.Failure(NotFound<TEntity>());
            }

            if (IsUntracked(context, entity))
            {
                return Result<TResult>.Failure(Untracked<TEntity>());
            }

            await PrepareAsync<TEntity>(context, entityId, cancellationToken);
            var workflow = WorkflowRegistry.Default.Get<TState, TEntity>();
            var held = await HeldTenantRefusalAsync(context, TenantOf(entity), cancellationToken);
            return Result<TResult>.Success(await judge(context, workflow, entity, EnvironmentFor(context, customFields: await CustomFieldsAsync(context, workflow, entity, cancellationToken)), held));
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            this.log.LogError(exception, "Reading the steps of {EntityType} {EntityId} failed.", typeof(TEntity).Name, entityId);
            return Result<TResult>.Failure($"The steps of the {typeof(TEntity).Name} could not be read. Try again.");
        }
    }

    private async Task<(string? Refusal, IReadOnlyList<string> Reasons)> JudgeTransitionAsync<TState, TEntity>(ApplicationDbContext context, Workflow<TState, TEntity> workflow, TEntity entity, Transition<TState, TEntity> transition, WorkflowEnvironment environment, string? held, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        var cascaded = new List<CascadedTransition>();
        await LoadCascadesAsync(context, entity, workflow, transition.Key, cascaded, cancellationToken);
        var reasons = entity.IsInTransition ? [Held<TEntity>()] : BlockingReasonsOrCannotBeChecked(workflow, entity, transition, environment, cancellationToken);

        IReadOnlyList<string> blocked = entity.IsInTransition ? [] : await DeleteBlockerReasonsAsync(context, entity, transition, cascaded, cancellationToken);
        if (blocked.Count > 0)
        {
            reasons = [.. reasons, .. blocked];
        }

        var refusal = await AccessRefusalAsync(transition, cancellationToken) ?? await CascadedAccessRefusalAsync(transition, cascaded, cancellationToken) ?? held;
        return (refusal, reasons);
    }

    private Task<string?> ListedAccessRefusalAsync(ITransition transition, CancellationToken cancellationToken) =>
        transition.IsSystemDriven ? Task.FromResult<string?>(SystemDrivenRefusal(NameOf(transition))) : AccessRefusalAsync(transition, cancellationToken);

    public IReadOnlySet<string> GetGraphPaths<TEntity>(Func<IQueryable<TEntity>, IQueryable<TEntity>>? queryConfigurator)
        where TEntity : class
    {
        using var context = this.dbFactory.CreateDbContext(this.runtime);
        return WorkflowGraphPaths.Of(context, queryConfigurator);
    }

    private IReadOnlyList<string> BlockingReasonsOrCannotBeChecked<TState, TEntity>(Workflow<TState, TEntity> workflow, TEntity entity, Transition<TState, TEntity> transition, WorkflowEnvironment environment, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        try
        {
            return workflow.GetBlockingReasons(entity, transition.Key, environment);
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            this.log.LogError(exception, "Judging {TransitionKey} on {EntityType} {EntityId} failed; the step is answered as one that cannot be checked.", transition.Key, typeof(TEntity).Name, entity.Id);
            return [StepCannotBeChecked];
        }
    }

    private async Task<(Transition<TState, TEntity>? Transition, string? Refusal)> RefuseStepWithEditsAsync<TState, TEntity>(DetachedEntityUpdater<TEntity> edits, Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> pick, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        var workflow = WorkflowRegistry.Default.Get<TState, TEntity>();
        var (transition, error) = pick(edits.Entity, workflow);
        if (transition is null)
        {
            return (null, error);
        }

        var loadedIn = (TState)edits.OriginalValue(nameof(IStateEntity<>.State))!;
        if (!EqualityComparer<TState>.Default.Equals(loadedIn, transition.From))
        {
            return (transition, $"The {typeof(TEntity).Name} is {loadedIn}, but '{transition.Key}' leaves {transition.From}. Reload it and try again.");
        }

        if (((Enum)transition.To).IsDeleteState())
        {
            return (transition, $"The step '{NameOf(transition)}' removes the {typeof(TEntity).Name} as it is stored, so it runs without the edits.");
        }

        if (transition.TakesNoEdits && edits.HasChanges())
        {
            this.log.LogInformation("Refused {TransitionKey} with edits on {EntityType} {EntityId} for user {UserId}: it takes no edits.", transition.Key, typeof(TEntity).Name, edits.OriginalValue(nameof(IStateEntity.Id)), this.runtime.EffectiveUserId);
            return (transition, TransitionRefusals.PendingEdits);
        }

        if (await AccessRefusalAsync(transition, cancellationToken) is { } refusal)
        {
            this.log.LogInformation("Refused {TransitionKey} with edits on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, edits.OriginalValue(nameof(IStateEntity.Id)), this.runtime.EffectiveUserId, refusal);
            return (transition, refusal);
        }

        if (edits.HasChanges() && await EditAuthorityRefusalAsync(workflow, transition, cancellationToken) is { } notYours)
        {
            this.log.LogInformation("Refused {TransitionKey} with edits on {EntityType} {EntityId} for user {UserId}: {Reason}", transition.Key, typeof(TEntity).Name, edits.OriginalValue(nameof(IStateEntity.Id)), this.runtime.EffectiveUserId, notYours);
            return (transition, notYours);
        }

        return (transition, null);
    }

    private async Task<string?> EditAuthorityRefusalAsync<TState, TEntity>(Workflow<TState, TEntity> workflow, Transition<TState, TEntity> transition, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        if (transition.IsSave)
        {
            return null;
        }

        if (workflow.GetEditAuthority(transition.From) is not { } authority)
        {
            return workflow.EditsGoWithItsSteps(transition.From) ? null : TransitionRefusals.NoEditsInState(typeof(TEntity).Name, DisplayNameOf(transition.From), NameOf(transition));
        }

        if (ReferenceEquals(authority, transition))
        {
            return null;
        }

        return await AccessRefusalAsync(authority, cancellationToken) is { } refusal
            ? TransitionRefusals.EditsRequireSaveAccess(typeof(TEntity).Name, NameOf(transition), refusal)
            : null;
    }

    private static async Task<string?> ChangedSinceOpenedAsync<TEntity>(ApplicationDbContext context, TEntity entity, DetachedEntityUpdater<TEntity> edits, ITransition transition, CancellationToken cancellationToken)
        where TEntity : class
    {
        var stored = await context.Entry(entity).GetDatabaseValuesAsync(cancellationToken);
        if (stored is null)
        {
            return NotFound<TEntity>();
        }

        return edits.ColumnsChangedSinceSnapshot(stored).Count > 0 ? TransitionRefusals.ChangedSinceOpened(typeof(TEntity).Name, NameOf(transition)) : null;
    }

    private async Task<string?> AccessRefusalAsync(ITransition transition, CancellationToken cancellationToken)
    {
        if (transition.RequiresPlatformOwner && !this.runtime.HasPlatformReach())
        {
            return $"Only a platform owner may take the step '{NameOf(transition)}'.";
        }

        if (transition.RequiredPermission is { } permission && !await this.permissionService.HasPermissionAsync(permission, cancellationToken))
        {
            return $"The step '{NameOf(transition)}' needs the permission '{DisplayNameOf(permission)}', which you do not have.";
        }

        return null;
    }

    private static async Task<string?> HeldTenantRefusalAsync(ApplicationDbContext context, Guid? tenantId, CancellationToken cancellationToken)
    {
        if (tenantId is not { } id)
        {
            return null;
        }

        var state = await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == id).Select(tenant => (TenantState?)tenant.State).FirstOrDefaultAsync(cancellationToken);
        return TenantStates.HoldsData(state) ? HeldTenant : null;
    }

    private static async Task<string?> HeldTenantRefusalInTransactionAsync(ApplicationDbContext context, Guid? tenantId, CancellationToken cancellationToken) =>
        tenantId is { } id && TenantStates.HoldsData(await context.HoldTenantAsync(id, cancellationToken)) ? HeldTenant : null;

    private async Task<string?> HeldTenantRefusalForCreateAsync<TEntity>(CancellationToken cancellationToken)
    {
        if (!typeof(ITenantEntity).IsAssignableFrom(typeof(TEntity)))
        {
            return null;
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await HeldTenantRefusalAsync(context, this.runtime.Current.TenantId, cancellationToken);
    }

    private static Guid? TenantOf(object entity) => entity switch
    {
        ITenantEntity owned => owned.TenantId,
        Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition field => field.TenantId,
        _ => null,
    };

    private async Task<string?> CascadedAccessRefusalAsync(ITransition primary, IEnumerable<CascadedTransition> cascaded, CancellationToken cancellationToken)
    {
        foreach (var step in cascaded.DistinctBy(step => step.Transition, ReferenceEqualityComparer.Instance))
        {
            if (await AccessRefusalAsync(step.Transition, cancellationToken) is { } refusal)
            {
                return $"The step '{NameOf(primary)}' also takes the step '{NameOf(step.Transition)}' on a {step.EntityType.Name}. {refusal}";
            }
        }

        return null;
    }

    private Func<IStateEntity, ITransition, string?> StepAccess(ApplicationDbContext context, ApprovedSteps approved, ITransition primary) =>
        (entity, step) =>
        {
            if (approved.Contains(entity, step))
            {
                return null;
            }

            var entityType = context.Model.FindRuntimeEntityType(entity.GetType())?.ClrType.Name ?? entity.GetType().Name;
            this.log.LogWarning("Refused {TransitionKey}: it reached {StepKey} on {EntityType} {EntityId}, a step its access gate did not judge.", primary.Key, step.Key, entityType, entity.Id);
            return $"The step '{primary.Key}' reached the step '{step.Key}' on a {entityType}, which was not checked before it ran, so nothing was changed.";
        };

    private async Task<IReadOnlyList<string>> DeleteBlockerReasonsAsync(ApplicationDbContext context, IStateEntity entity, ITransition transition, IEnumerable<CascadedTransition> cascaded, CancellationToken cancellationToken)
    {
        var deleted = new List<IStateEntity>();
        if (transition.To.IsDeleteState())
        {
            deleted.Add(entity);
        }

        deleted.AddRange(cascaded.Where(step => step.Transition.To.IsDeleteState()).Select(step => step.Entity));
        var reasons = new List<string>();
        foreach (var candidate in deleted.Distinct(ReferenceEqualityComparer.Instance).Cast<IStateEntity>())
        {
            var entityType = ModelTypeOf(context, candidate);
            foreach (var blocker in this.deleteBlockers.Where(blocker => blocker.EntityType.IsAssignableFrom(entityType)))
            {
                if (await blocker.FindUseAsync(context, candidate, cancellationToken) is { Length: > 0 } reason)
                {
                    reasons.Add(reason);
                }
            }
        }

        return reasons;
    }

    private async Task PrepareAsync<TEntity>(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))?.ClrType ?? typeof(TEntity);
        foreach (var preparation in this.preparations.Where(preparation => preparation.EntityType.IsAssignableFrom(entityType)))
        {
            await preparation.PrepareAsync(context, entityId, cancellationToken);
        }
    }

    private async Task PrepareEditedAsync<TEntity>(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))?.ClrType ?? typeof(TEntity);
        foreach (var preparation in this.preparations.Where(preparation => preparation.EntityType.IsAssignableFrom(entityType)))
        {
            await preparation.PrepareEditedAsync(context, entityId, cancellationToken);
        }
    }

    private void ForgetEdited<TEntity>(Guid entityId)
    {
        foreach (var preparation in this.preparations.Where(preparation => preparation.EntityType.IsAssignableFrom(typeof(TEntity))))
        {
            try
            {
                preparation.ForgetEdited(entityId);
            }
            catch (Exception exception)
            {
                this.log.LogError(exception, "A preparation of {EntityType} could not let go of what it read of {EntityId} as edited.", typeof(TEntity).Name, entityId);
            }
        }
    }

    private async Task PrepareNewAsync<TEntity>(ApplicationDbContext context, TEntity entity, CancellationToken cancellationToken)
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))?.ClrType ?? typeof(TEntity);
        foreach (var preparation in this.preparations.Where(preparation => preparation.EntityType.IsAssignableFrom(entityType)))
        {
            await preparation.PrepareNewAsync(context, entity, cancellationToken);
        }
    }

    private WorkflowEnvironment EnvironmentFor(ApplicationDbContext context, Func<IStateEntity, ITransition, string?>? stepAccess = null, ICustomFieldCatalog? customFields = null) =>
        new(this.clock, NavigationLoadState.From(context), this.services, stepAccess, customFields);

    private static readonly MethodInfo readCustomFields =
        typeof(WorkflowService).GetMethod(nameof(ReadCustomFieldsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private async Task<ICustomFieldCatalog?> CustomFieldsAsync<TState, TEntity>(ApplicationDbContext context, Workflow<TState, TEntity> workflow, TEntity entity, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        var tables = workflow.Transitions.SelectMany(transition => transition.Conditions).OfType<ICustomFieldCondition>().Select(condition => condition.DefinitionType).Distinct().ToList();
        if (tables.Count == 0)
        {
            return null;
        }

        var entry = context.Entry(entity);
        if (entry.State is EntityState.Unchanged or EntityState.Modified)
        {
            foreach (var collection in Staffora.Core.Entities.EntityAttributes.CustomFieldCollection.Of(typeof(TEntity)).Where(collection => tables.Contains(collection.DefinitionType)))
            {
                var values = entry.Collection(collection.Navigation.Name);
                if (!values.IsLoaded)
                {
                    await values.LoadAsync(cancellationToken);
                }
            }
        }

        var tenant = entity is ITenantEntity { TenantId: var own } && own != Guid.Empty ? own : this.runtime.Current.TenantId;
        var fields = new Dictionary<Type, IReadOnlyList<Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition>>();
        foreach (var table in tables)
        {
            fields[table] = tenant is { } id
                ? await (Task<IReadOnlyList<Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition>>)readCustomFields.MakeGenericMethod(table).Invoke(null, [context, id, cancellationToken])!
                : [];
        }

        return new CustomFieldCatalog(fields);
    }

    private static async Task<IReadOnlyList<Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition>> ReadCustomFieldsAsync<TDefinition>(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
        where TDefinition : class, Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition
    {
        var rows = await context.Set<TDefinition>().AsNoTracking()
            .Where(definition => (definition.TenantId == null || definition.TenantId == tenantId) && definition.State == Staffora.Core.Entities.EntityAttributes.CustomFieldState.Active)
            .OrderBy(definition => definition.Name)
            .ThenBy(definition => definition.Id)
            .ToListAsync(cancellationToken);
        return [.. rows];
    }

    private sealed class ApprovedSteps
    {
        private readonly Dictionary<IStateEntity, HashSet<ITransition>> steps = new(ReferenceEqualityComparer.Instance);

        public void Add(IStateEntity entity, ITransition transition)
        {
            if (!this.steps.TryGetValue(entity, out var transitions))
            {
                this.steps[entity] = transitions = new HashSet<ITransition>(ReferenceEqualityComparer.Instance);
            }

            transitions.Add(transition);
        }

        public bool Contains(IStateEntity entity, ITransition transition) => this.steps.TryGetValue(entity, out var transitions) && transitions.Contains(transition);
    }
}
