using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Staffora.Core;
using Staffora.Core.Entities;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Actions;
using Staffora.Data;

namespace Staffora.Services.Workflow;

public sealed partial class WorkflowService
{
    private static readonly MethodInfo lockRows =
        typeof(WorkflowService).GetMethod(nameof(LockRowsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo rowVersions =
        typeof(WorkflowService).GetMethod(nameof(ReadRowVersionsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void RefuseStateWrittenOutsideTheEngine(ApplicationDbContext context, TransitionOutcome outcome, LockCandidates candidates)
    {
        var steps = new Dictionary<IStateEntity, TransitionStep>(ReferenceEqualityComparer.Instance);
        foreach (var step in outcome.Steps)
        {
            steps[step.Entity] = step;
        }

        foreach (var entry in context.ChangeTracker.Entries().Where(entry => entry.Entity is IStateEntity && entry.State != EntityState.Added))
        {
            var entity = (IStateEntity)entry.Entity;
            var state = entry.Property(nameof(IStateEntity<>.State));
            var held = entry.Property(nameof(IStateEntity.IsInTransition));

            var loaded = state.OriginalValue;
            var expected = steps.TryGetValue(entity, out var moved) ? (Equals(moved.From, loaded) ? moved.To : null) : loaded;
            var released = candidates.Contains(entity) ? held.CurrentValue is false : Equals(held.CurrentValue, held.OriginalValue);
            if (expected is null || !Equals(expected, state.CurrentValue) || !released)
            {
                throw new StateWrittenOutsideTheEngineException(ModelTypeOf(context, entity).Name, entity.Id);
            }
        }
    }

    private static async Task LockAsync(ApplicationDbContext context, LockCandidates candidates, CancellationToken cancellationToken)
    {
        var groups = candidates.All
            .GroupBy(entity => ModelTypeOf(context, entity))
            .OrderBy(group => group.Key.FullName, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var entities = group.ToList();
            var ids = entities.Select(entity => entity.Id).Distinct().ToList();

            var tenantIds = typeof(ITenantEntity).IsAssignableFrom(group.Key)
                ? entities.Cast<ITenantEntity>().Select(entity => entity.TenantId).Distinct().ToList()
                : null;
            var locked = await (Task<int>)lockRows.MakeGenericMethod(group.Key).Invoke(null, [context, ids, tenantIds, cancellationToken])!;
            if (locked < ids.Count)
            {
                throw new TransitionLockedException(group.Key.Name);
            }

            foreach (var entity in entities)
            {
                context.Entry(entity).Property(nameof(IStateEntity.IsInTransition)).OriginalValue = true;
            }

            foreach (var version in context.Model.FindEntityType(group.Key)!.GetProperties().Where(IsRowVersion))
            {
                var stored = await (Task<Dictionary<Guid, byte[]>>)rowVersions.MakeGenericMethod(group.Key).Invoke(null, [context, ids, version.Name, cancellationToken])!;
                foreach (var entity in entities)
                {
                    var property = context.Entry(entity).Property(version.Name);
                    property.CurrentValue = stored[entity.Id];
                    property.OriginalValue = stored[entity.Id];
                }
            }
        }
    }

    private static bool IsRowVersion(Microsoft.EntityFrameworkCore.Metadata.IProperty property) =>
        property.IsConcurrencyToken && property.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate && property.ClrType == typeof(byte[]);

    private static async Task<Dictionary<Guid, byte[]>> ReadRowVersionsAsync<TRow>(ApplicationDbContext context, List<Guid> ids, string column, CancellationToken cancellationToken)
        where TRow : class =>
        await context.Set<TRow>().AsNoTracking()
            .Where(row => ids.Contains(EF.Property<Guid>(row, nameof(IStateEntity.Id))))
            .Select(row => new { Id = EF.Property<Guid>(row, nameof(IStateEntity.Id)), Version = EF.Property<byte[]>(row, column) })
            .ToDictionaryAsync(row => row.Id, row => row.Version, cancellationToken);

    private async Task RemoveDeletedAsync(ApplicationDbContext context, TransitionOutcome outcome, CancellationToken cancellationToken)
    {
        foreach (var entity in outcome.LeftInDeleteState.Reverse())
        {
            if (context.Entry(entity).State == EntityState.Deleted)
            {
                continue;
            }

            var entityType = ModelTypeOf(context, entity);
            foreach (var cleanup in this.deleteCleanups.Where(cleanup => cleanup.EntityType.IsAssignableFrom(entityType)))
            {
                await cleanup.BeforeRemoveAsync(context, entity, cancellationToken);
            }

            context.Remove(entity);
        }
    }

    private async Task<IDbContextTransaction?> BeginLockedAsync<TEntity>(ApplicationDbContext context, Guid? entityId, CancellationToken cancellationToken, bool inBatch = false)
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))?.ClrType ?? typeof(TEntity);
        var locks = this.stepLocks.Where(stepLock => stepLock.EntityType.IsAssignableFrom(entityType)).ToList();
        if (locks.Count == 0)
        {
            return null;
        }

        if (inBatch)
        {
            foreach (var stepLock in locks)
            {
                await stepLock.TakeAsync(context, entityId, cancellationToken);
            }

            return null;
        }

        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            foreach (var stepLock in locks)
            {
                await stepLock.TakeAsync(context, entityId, cancellationToken);
            }

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static Task LoadCascadesAsync(ApplicationDbContext context, IStateEntity entity, IWorkflow workflow, string transitionKey, ICollection<CascadedTransition> cascaded, CancellationToken cancellationToken) =>
        LoadCascadesAsync(context, entity, workflow.GetCascades(transitionKey), cascaded, new HashSet<IStateEntity>(ReferenceEqualityComparer.Instance), cancellationToken);

    private static async Task LoadCascadesAsync(ApplicationDbContext context, IStateEntity entity, IReadOnlyList<ICascadeAction> cascades, ICollection<CascadedTransition> cascaded, HashSet<IStateEntity> walked, CancellationToken cancellationToken)
    {
        if (!walked.Add(entity))
        {
            return;
        }

        foreach (var cascade in cascades)
        {
            var childWorkflow = WorkflowRegistry.Default.Get(cascade.ChildType);
            foreach (var child in await LoadCascadeNavigationAsync(context, entity, cascade.Navigation, cancellationToken))
            {
                var state = (Enum)context.Entry(child).Property(nameof(IStateEntity<>.State)).CurrentValue!;
                if (state.Equals(cascade.Target))
                {
                    if (state.IsDeleteState())
                    {
                        await LoadCascadesAsync(context, child, childWorkflow.GetStateCascades(state), cascaded, walked, cancellationToken);
                    }

                    continue;
                }

                if (cascade.LeaveAlone.Contains(state))
                {
                    continue;
                }

                if (childWorkflow.Transitions.Where(transition => transition.From.Equals(state) && transition.To.Equals(cascade.Target) && !transition.NeverCascaded).ToList() is [var next])
                {
                    cascaded.Add(new CascadedTransition(cascade.ChildType, child, next));
                    await LoadCascadesAsync(context, child, childWorkflow.GetCascades(next.Key), cascaded, walked, cancellationToken);
                }
            }
        }
    }

    private static async Task<IReadOnlyList<IStateEntity>> LoadCascadeNavigationAsync(ApplicationDbContext context, object entity, string navigation, CancellationToken cancellationToken)
    {
        var segments = navigation.Split('.');
        var current = entity;
        foreach (var segment in segments[..^1])
        {
            var reference = context.Entry(current).Reference(segment);
            if (!reference.IsLoaded)
            {
                await reference.LoadAsync(cancellationToken);
            }

            if (reference.CurrentValue is not { } next)
            {
                return [];
            }

            current = next;
        }

        var collection = context.Entry(current).Collection(segments[^1]);
        if (!collection.IsLoaded)
        {
            await collection.LoadAsync(cancellationToken);
        }

        return [.. (collection.CurrentValue ?? Array.Empty<object>()).Cast<IStateEntity>().Where(child => segments.Length == 1 || !ReferenceEquals(child, entity))];
    }

    private static Type ModelTypeOf(ApplicationDbContext context, IStateEntity entity) =>
        context.Model.FindRuntimeEntityType(entity.GetType())?.ClrType
            ?? throw new InvalidOperationException($"{entity.GetType().Name} is named by a transition but is not an entity type of the model.");

    private static Task<int> LockRowsAsync<TRow>(ApplicationDbContext context, List<Guid> ids, List<Guid>? tenantIds, CancellationToken cancellationToken)
        where TRow : class
    {
        var rows = context.Set<TRow>().Where(row => ids.Contains(EF.Property<Guid>(row, nameof(IStateEntity.Id))) && !EF.Property<bool>(row, nameof(IStateEntity.IsInTransition)));
        if (tenantIds is not null)
        {
            rows = rows.Where(row => tenantIds.Contains(EF.Property<Guid>(row, nameof(ITenantEntity.TenantId))));
        }

        return rows.ExecuteUpdateAsync(set => set.SetProperty(row => EF.Property<bool>(row, nameof(IStateEntity.IsInTransition)), true), cancellationToken);
    }

    private sealed record CascadedTransition(Type EntityType, IStateEntity Entity, ITransition Transition);

    private sealed class TransitionLockedException : InvalidOperationException
    {
        public TransitionLockedException(string entityType) : base($"A {entityType} named by the transition is held by another transition or no longer exists.")
        {
        }
    }

    private sealed class StateWrittenOutsideTheEngineException : InvalidOperationException
    {
        public StateWrittenOutsideTheEngineException(string entityType, Guid entityId) : base($"An action wrote State or IsInTransition of {entityType} {entityId} itself; only the workflow engine moves a state entity.")
        {
        }
    }
}
