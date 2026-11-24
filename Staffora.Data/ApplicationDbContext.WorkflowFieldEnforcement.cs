using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Staffora.Core;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Data;

public partial class ApplicationDbContext
{
    private static readonly HashSet<string> notWorkflowFields = new(StringComparer.Ordinal)
    {
        nameof(ITenantEntity.TenantId),
        nameof(ITenantEntity.Created),
        nameof(ITenantEntity.CreatedById),
        nameof(ITenantEntity.Modified),
        nameof(ITenantEntity.ModifiedById),
        nameof(ITenantEntity.ImpersonatedById),
    };

    private static readonly string[] transitionFields = [nameof(IStateEntity<>.State), nameof(IStateEntity.IsInTransition)];

    private static readonly MethodInfo storedParentsQuery =
        typeof(ApplicationDbContext).GetMethod(nameof(ReadStoredParentsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo storedChildrenCount =
        typeof(ApplicationDbContext).GetMethod(nameof(CountStoredChildrenAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Dictionary<IStateEntity, int> transitionSaveScopes = new(ReferenceEqualityComparer.Instance);

    public IDisposable BeginTransitionSaveScope(IEnumerable<IStateEntity> entities, string reason)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var scoped = new List<IStateEntity>();
        foreach (var entity in entities.Distinct(ReferenceEqualityComparer.Instance).Cast<IStateEntity>())
        {
            this.transitionSaveScopes[entity] = this.transitionSaveScopes.GetValueOrDefault(entity) + 1;
            scoped.Add(entity);
        }

        IsolationLog().LogInformation("Transition save scope opened for {Reason} over {Count} entities.", reason, scoped.Count);
        return new TransitionSaveScope(this, scoped, reason);
    }

    public bool IsInTransitionSaveScope(IStateEntity entity) => this.transitionSaveScopes.GetValueOrDefault(entity) > 0;

    private async Task EnforceWorkflowFieldsAsync(IReadOnlyDictionary<object, PropertyValues?> storedRows, bool synchronously, CancellationToken cancellationToken)
    {
        RefuseHistoryRewrites();
        RefuseIntegrationMessageWrites();
        RefuseInsertsOutsideEntryState();

        var writes = ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted && entry.Entity is IStateEntity entity && !IsInTransitionSaveScope(entity))
            .ToList();

        foreach (var entry in writes)
        {
            var stored = storedRows.TryGetValue(entry.Entity, out var read)
                ? read
                : synchronously ? entry.GetDatabaseValues() : await entry.GetDatabaseValuesAsync(cancellationToken);

            if (stored is not null && stored.GetValue<bool>(nameof(IStateEntity.IsInTransition)))
            {
                RefuseHeldRow(entry, stored);
            }

            if (entry.State == EntityState.Deleted)
            {
                RefuseDeleteOutsideTransition(entry, stored);
            }

            if (stored is not null && entry.State == EntityState.Modified)
            {
                JudgeUpdate(entry, stored);
            }
        }

        await JudgeChildWritesAsync(storedRows, synchronously, cancellationToken);
    }

    private void RefuseHeldRow(EntityEntry entry, PropertyValues stored)
    {
        var (operation, entity, key) = Describe(entry);
        var persistedState = (Enum)stored[nameof(IStateEntity<>.State)]!;
        const string reason = "a transition holds it, so only that transition writes it until it is released.";
        IsolationLog().LogWarning("Refused {Operation} on {EntityType} {Key}: {Reason}", operation, entity, key, reason);
        throw new WorkflowFieldEditException(entity, key, persistedState, [], $"Refused to {operation} {entity} {key}: {reason}", heldByTransition: true);
    }

    private void RefuseDeleteOutsideTransition(EntityEntry entry, PropertyValues? stored)
    {
        var (_, entity, key) = Describe(entry);
        var persistedState = (Enum?)stored?[nameof(IStateEntity<>.State)];
        const string reason = "a state entity is removed only by its delete transition, in that transition's save (ADR 0016).";
        IsolationLog().LogWarning("Refused delete on {EntityType} {Key}: {Reason}", entity, key, reason);
        throw new WorkflowFieldEditException(entity, key, persistedState, [], $"Refused to delete {entity} {key}: {reason}");
    }

    private void RefuseHistoryRewrites()
    {
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.Entity is StateHistoryEntry or Comment or CommentContainer || NoticeBookkeepingColumns.ContainsKey(entry.Entity.GetType())))
        {
            if (entry.State != EntityState.Modified && (entry.State != EntityState.Deleted || IsInSystemScope))
            {
                continue;
            }

            var (operation, entity, key) = Describe(entry);
            List<string> fields = entry.State == EntityState.Modified
                ? entry.Properties.Where(property => property.IsModified && !notWorkflowFields.Contains(property.Metadata.Name)).Select(property => property.Metadata.Name).ToList()
                : [];

            var bookkeeping = NoticeBookkeepingColumns.GetValueOrDefault(entry.Entity.GetType());
            if (bookkeeping is not null && entry.State == EntityState.Modified)
            {
                fields = entry.Properties
                    .Where(property => property.IsModified && !notWorkflowFields.Contains(property.Metadata.Name) && !bookkeeping.Contains(property.Metadata.Name) && !property.Metadata.GetValueComparer().Equals(property.OriginalValue, property.CurrentValue))
                    .Select(property => property.Metadata.Name)
                    .ToList();
                if (fields.Count == 0)
                {
                    continue;
                }
            }

            var what = entry.Entity switch
            {
                StateHistoryEntry => "state history is",
                OutgoingMessage => "a queued notice is",
                StoredMessage => "a delivered notice is",
                MessageRecipient => "a person's copy of a notice is",
                IntegrationMessage => "an integration message is",
                IntegrationReceipt => "an integration message's receipt is",
                _ => "a comment thread is",
            };
            var reason = entry.State != EntityState.Modified
                ? $"{what} deleted only in system scope."
                : bookkeeping is { Count: > 0 }
                    ? $"{what} changed only in {string.Join(", ", bookkeeping)}, never in {string.Join(", ", fields)}."
                    : $"{what} never edited.";
            IsolationLog().LogWarning("Refused {Operation} on {EntityType} {Key}: {Reason}", operation, entity, key, reason);
            throw new WorkflowFieldEditException(entity, key, null, fields, $"Refused to {operation} {entity} {key}: {reason}");
        }
    }

    private void RefuseIntegrationMessageWrites()
    {
        foreach (var entry in ChangeTracker.Entries<IntegrationMessage>())
        {
            var message = entry.Entity;
            string? reason = entry.State switch
            {
                EntityState.Added when message.State != IntegrationMessageState.Pending || message.Attempts != 0 || message.ClaimToken is not null
                    || message.ClaimedUntil is not null || message.Finished is not null || message.LastFailure is not null
                    => "an integration message is queued new: pending, with no attempt, claim, end or failure.",
                EntityState.Modified when !IsInSystemScope && entry.Properties.Any(property => property.IsModified && !notWorkflowFields.Contains(property.Metadata.Name) && !property.Metadata.GetValueComparer().Equals(property.OriginalValue, property.CurrentValue))
                    => "an integration message's delivery is written only by the outbox's dispatcher, under its leases.",
                _ => null,
            };
            if (reason is null)
            {
                continue;
            }

            var (operation, entity, key) = Describe(entry);
            IsolationLog().LogWarning("Refused {Operation} on {EntityType} {Key}: {Reason}", operation, entity, key, reason);
            throw new WorkflowFieldEditException(entity, key, null, [], $"Refused to {operation} {entity} {key}: {reason}");
        }
    }

    public static IReadOnlyDictionary<Type, IReadOnlySet<string>> NoticeBookkeepingColumns { get; } = new Dictionary<Type, IReadOnlySet<string>>
    {
        [typeof(OutgoingMessage)] = new HashSet<string>(StringComparer.Ordinal) { nameof(OutgoingMessage.Status), nameof(OutgoingMessage.Delivered), nameof(OutgoingMessage.RecipientCount), nameof(OutgoingMessage.Attempts), nameof(OutgoingMessage.NextAttempt) },
        [typeof(StoredMessage)] = new HashSet<string>(StringComparer.Ordinal),
        [typeof(MessageRecipient)] = new HashSet<string>(StringComparer.Ordinal) { nameof(MessageRecipient.IsRead), nameof(MessageRecipient.ReadAt) },
        [typeof(IntegrationMessage)] = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IntegrationMessage.State), nameof(IntegrationMessage.Attempts), nameof(IntegrationMessage.NextAttempt), nameof(IntegrationMessage.ClaimToken),
            nameof(IntegrationMessage.ClaimedUntil), nameof(IntegrationMessage.Finished), nameof(IntegrationMessage.LastFailure),
        },
        [typeof(IntegrationReceipt)] = new HashSet<string>(StringComparer.Ordinal),
    };

    private void RefuseInsertsOutsideEntryState()
    {
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added && entry.Entity is IStateEntity entity && !IsInTransitionSaveScope(entity)))
        {
            if ((bool)entry.Property(nameof(IStateEntity.IsInTransition)).CurrentValue!)
            {
                var (_, held, heldKey) = Describe(entry);
                const string heldReason = "a new record starts released; only the transition that locks a row holds it.";
                IsolationLog().LogWarning("Refused insert on {EntityType} {Key}: {Reason}", held, heldKey, heldReason);
                throw new WorkflowFieldEditException(held, heldKey, null, [nameof(IStateEntity.IsInTransition)], $"Refused to insert {held} {heldKey}: {heldReason}");
            }

            var state = (Enum)entry.Property(nameof(IStateEntity<>.State)).CurrentValue!;
            if (IsInSystemScope || state.IsEntryState())
            {
                continue;
            }

            var (_, entity, key) = Describe(entry);
            var entryStates = Enum.GetValues(state.GetType()).Cast<Enum>().Where(member => member.IsEntryState()).ToList();
            var reason = entryStates.Count == 0
                ? $"a new {entity} starts in its entry state, and {state.GetType().Name} marks none ([EntryState], or a member named Create)."
                : $"a new {entity} starts in {string.Join(" or ", entryStates)} and reaches {state} only through its workflow.";
            IsolationLog().LogWarning("Refused insert on {EntityType} {Key}: {Reason}", entity, key, reason);
            throw new WorkflowFieldEditException(entity, key, null, [nameof(IStateEntity<>.State)], $"Refused to insert {entity} {key}: {reason}");
        }
    }

    private void JudgeUpdate(EntityEntry entry, PropertyValues stored)
    {
        var persistedState = (Enum)stored[nameof(IStateEntity<>.State)]!;
        var workflow = WorkflowRegistry.Default.Get(entry.Metadata.ClrType);
        var refused = new List<string>();
        var emptied = new List<string>();

        foreach (var property in entry.Properties.Where(property => property.IsModified))
        {
            var metadata = property.Metadata;
            if (metadata.GetValueComparer().Equals(stored[metadata.Name], property.CurrentValue))
            {
                continue;
            }

            var isTransitionField = transitionFields.Contains(metadata.Name);
            var isWorkflowField = !metadata.IsPrimaryKey() && !metadata.IsShadowProperty() && !notWorkflowFields.Contains(metadata.Name);
            var access = isWorkflowField ? workflow.GetFieldAccess(persistedState, metadata.Name) : FieldAccessFlags.None;
            if (isTransitionField || (isWorkflowField && !access.HasFlag(FieldAccessFlags.Editable)))
            {
                refused.Add(metadata.Name);
            }
            else if (isWorkflowField && access.HasFlag(FieldAccessFlags.Mandatory) && IsEmpty(property.CurrentValue))
            {
                refused.Add(metadata.Name);
                emptied.Add(metadata.Name);
            }
        }

        if (refused.Count == 0)
        {
            return;
        }

        var entity = entry.Metadata.ClrType.Name;
        var key = string.Join(",", entry.Metadata.FindPrimaryKey()?.Properties.Select(property => entry.Property(property.Name).CurrentValue) ?? []);
        var locked = refused.Except(transitionFields).Except(emptied).ToList();
        var reasons = new List<string>();
        if (refused.Intersect(transitionFields).Any())
        {
            reasons.Add($"{string.Join(" and ", refused.Intersect(transitionFields))} change only through a transition.");
        }

        if (locked.Count > 0)
        {
            reasons.Add($"{string.Join(", ", locked)} cannot be edited while it is {persistedState}.");
        }

        if (emptied.Count > 0)
        {
            reasons.Add($"{string.Join(", ", emptied)} must have a value while it is {persistedState}.");
        }

        var reason = string.Join(" ", reasons);
        IsolationLog().LogWarning("Refused update on {EntityType} {Key}: {Reason}", entity, key, reason);
        throw new WorkflowFieldEditException(entity, key, persistedState, refused, $"Refused to update {entity} {key}: {reason}", emptiedRequiredFields: emptied);
    }

    internal static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        Guid id => id == Guid.Empty,
        _ => false,
    };

    private async Task JudgeChildWritesAsync(IReadOnlyDictionary<object, PropertyValues?> storedRows, bool synchronously, CancellationToken cancellationToken)
    {
        var pending = ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var children = new List<(EntityEntry Child, INavigation Collection, Guid Parent)>();
        foreach (var entry in pending.Where(entry => (entry.Entity is not IStateEntity self || !IsInTransitionSaveScope(self)) && WritesAnything(entry, storedRows)))
        {
            foreach (var foreignKey in entry.Metadata.GetForeignKeys())
            {
                if (DeclaredCollection(foreignKey) is { } collection)
                {
                    children.AddRange(ParentsOf(entry, foreignKey, storedRows).Select(parent => (entry, collection, parent)));
                }
            }
        }

        if (children.Count == 0)
        {
            return;
        }

        var tracked = ChangeTracker.Entries()
            .Where(entry => entry.Entity is IStateEntity)
            .GroupBy(entry => (entry.Metadata.GetRootType().ClrType, ((IStateEntity)entry.Entity).Id))
            .ToDictionary(group => group.Key, group => group.First());
        var wanted = children
            .Select(child => (Type: child.Collection.DeclaringEntityType, child.Parent))
            .Where(parent => !tracked.TryGetValue((parent.Type.GetRootType().ClrType, parent.Parent), out var entry) || entry.State == EntityState.Unchanged)
            .GroupBy(parent => parent.Type, parent => parent.Parent)
            .ToList();
        var stored = new Dictionary<(Type, Guid), StoredParent>();
        foreach (var table in wanted)
        {
            var workflow = WorkflowRegistry.Default.Get(table.Key.ClrType);
            var read = (Task<List<StoredParent>>)storedParentsQuery.MakeGenericMethod(table.Key.ClrType, workflow.StateType)
                .Invoke(null, [this, table.Distinct().ToList(), synchronously, cancellationToken])!;
            foreach (var row in await read)
            {
                stored[(table.Key.GetRootType().ClrType, row.Key)] = row;
            }
        }

        var emptying = new List<(INavigation Collection, Guid Parent, Enum State)>();
        var removedValues = new List<(EntityEntry Value, INavigation Collection, Guid Parent, Enum State)>();
        foreach (var (child, collection, parent) in children)
        {
            var parentType = collection.DeclaringEntityType;
            var key = (parentType.GetRootType().ClrType, parent);
            Enum state;
            if (tracked.TryGetValue(key, out var parentEntry) && parentEntry.State != EntityState.Unchanged)
            {
                if (IsInTransitionSaveScope((IStateEntity)parentEntry.Entity))
                {
                    continue;
                }

                if (parentEntry.State == EntityState.Added && IsAttributeValue(child.Metadata.ClrType) && !IsInSystemScope)
                {
                    state = (Enum)parentEntry.Property(nameof(IStateEntity<>.State)).CurrentValue!;
                }
                else if (parentEntry.State != EntityState.Modified)
                {
                    continue;
                }
                else
                {
                    state = (Enum)parentEntry.Property(nameof(IStateEntity<>.State)).OriginalValue!;
                }
            }
            else if (parentEntry is not null && IsInTransitionSaveScope((IStateEntity)parentEntry.Entity))
            {
                continue;
            }
            else if (stored.TryGetValue(key, out var row))
            {
                if (row.IsInTransition)
                {
                    throw RefuseChildWrite(child, collection, parent, row.State, "a transition holds it, so only that transition writes it until it is released.", heldByTransition: true);
                }

                state = row.State;
            }
            else
            {
                continue;
            }

            var access = WorkflowRegistry.Default.Get(parentType.ClrType).GetFieldAccess(state, collection.Name);
            if (!access.HasFlag(FieldAccessFlags.Editable))
            {
                throw RefuseChildWrite(child, collection, parent, state, $"its {LabelOf(collection)} cannot be edited while it is {state}.", heldByTransition: false);
            }

            if (access.HasFlag(FieldAccessFlags.Mandatory) && LeavesFrom(child, collection.ForeignKey, parent, storedRows))
            {
                emptying.Add((collection, parent, state));
            }

            if (child.State == EntityState.Deleted
                && IsAttributeValue(child.Metadata.ClrType)
                && CustomFieldCollection.DefinitionTypeOf(child.Metadata.ClrType) is { } table
                && WorkflowRegistry.Default.Get(parentType.ClrType).RequiresCustomFieldsIn(state, table))
            {
                removedValues.Add((child, collection, parent, state));
            }
        }

        await RefuseEmptiedRequiredCollectionsAsync(emptying, pending, storedRows, synchronously, cancellationToken);
        await RefuseEmptiedRequiredCustomFieldsAsync(removedValues, synchronously, cancellationToken);
    }

    private static readonly MethodInfo requiredDefinitionsQuery =
        typeof(ApplicationDbContext).GetMethod(nameof(ReadRequiredDefinitionsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private async Task RefuseEmptiedRequiredCustomFieldsAsync(List<(EntityEntry Value, INavigation Collection, Guid Parent, Enum State)> removed, bool synchronously, CancellationToken cancellationToken)
    {
        foreach (var table in removed.GroupBy(candidate => CustomFieldCollection.DefinitionTypeOf(candidate.Value.Metadata.ClrType)!))
        {
            var ids = table.Select(candidate => (Guid)candidate.Value.Property(nameof(IEntityAttributeValue.EntityAttributeDefinitionId)).OriginalValue!).Distinct().ToList();
            var required = await (Task<List<IEntityAttributeDefinition>>)requiredDefinitionsQuery.MakeGenericMethod(table.Key)
                .Invoke(null, [this, ids, synchronously, cancellationToken])!;
            foreach (var owner in table.GroupBy(candidate => candidate.Parent))
            {
                var emptied = new List<string>();
                foreach (var (value, _, _, _) in owner)
                {
                    var field = (Guid)value.Property(nameof(IEntityAttributeValue.EntityAttributeDefinitionId)).OriginalValue!;
                    if (required.FirstOrDefault(definition => definition.Id == field) is { } definition)
                    {
                        emptied.Add(definition.Name);
                    }
                }

                if (emptied.Count == 0)
                {
                    continue;
                }

                var (first, collection, parent, state) = owner.First();
                var (operation, entity, key) = Describe(first);
                var parentType = collection.DeclaringEntityType.ClrType.Name;
                var names = emptied.Order(StringComparer.Ordinal).ToList();
                var reason = $"{string.Join(", ", names)} must have a value while it is {state}.";
                IsolationLog().LogWarning("Refused {Operation} on {EntityType} {Key} in {ParentType} {ParentKey}.{Collection}: {Reason}", operation, entity, key, parentType, parent, collection.Name, reason);
                throw new WorkflowFieldEditException(parentType, parent.ToString(), state, [collection.Name], $"Refused to {operation} {entity} {key} in {parentType} {parent}: {reason}", emptiedRequiredFields: [collection.Name], emptiedCustomFields: names);
            }
        }
    }

    private static async Task<List<IEntityAttributeDefinition>> ReadRequiredDefinitionsAsync<TDefinition>(ApplicationDbContext context, List<Guid> ids, bool synchronously, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        var query = context.Set<TDefinition>().IgnoreQueryFilters().AsNoTracking().Where(definition => ids.Contains(definition.Id) && definition.IsRequired && definition.State == CustomFieldState.Active);
        var rows = synchronously ? query.ToList() : await query.ToListAsync(cancellationToken);
        return [.. rows.Where(definition => Enum.IsDefined(definition.ValueType))];
    }

    private async Task RefuseEmptiedRequiredCollectionsAsync(List<(INavigation Collection, Guid Parent, Enum State)> emptying, List<EntityEntry> pending, IReadOnlyDictionary<object, PropertyValues?> storedRows, bool synchronously, CancellationToken cancellationToken)
    {
        foreach (var (collection, parent, state) in emptying.DistinctBy(candidate => (candidate.Collection, candidate.Parent)))
        {
            var foreignKey = collection.ForeignKey;
            var rows = pending.Where(entry => entry.Metadata.GetForeignKeys().Contains(foreignKey)).ToList();
            var leaving = rows.Count(entry => LeavesFrom(entry, foreignKey, parent, storedRows));
            var joining = rows.Count(entry => JoinsUnder(entry, foreignKey, parent, storedRows));
            var stored = await (Task<int>)storedChildrenCount.MakeGenericMethod(collection.TargetEntityType.ClrType)
                .Invoke(null, [this, foreignKey.Properties[0], parent, synchronously, cancellationToken])!;
            if (stored - leaving + joining > 0)
            {
                continue;
            }

            var child = rows.First(entry => LeavesFrom(entry, foreignKey, parent, storedRows));
            var (operation, entity, key) = Describe(child);
            var parentType = collection.DeclaringEntityType.ClrType.Name;
            var reason = $"its {LabelOf(collection)} must have a row while it is {state}.";
            IsolationLog().LogWarning("Refused {Operation} on {EntityType} {Key} in {ParentType} {ParentKey}.{Collection}: {Reason}", operation, entity, key, parentType, parent, collection.Name, reason);
            throw new WorkflowFieldEditException(parentType, parent.ToString(), state, [collection.Name], $"Refused to {operation} {entity} {key} in {parentType} {parent}: {reason}", emptiedRequiredFields: [collection.Name]);
        }
    }

    private static string LabelOf(INavigation collection) =>
        collection.PropertyInfo?.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetName() ?? collection.Name;

    private static bool LeavesFrom(EntityEntry entry, IForeignKey foreignKey, Guid parent, IReadOnlyDictionary<object, PropertyValues?> storedRows) =>
        entry.State switch
        {
            EntityState.Deleted => Equals(StoredParentOf(entry, foreignKey, storedRows), parent),
            EntityState.Modified => Equals(StoredParentOf(entry, foreignKey, storedRows), parent) && !Equals(entry.Property(foreignKey.Properties[0].Name).CurrentValue, parent),
            _ => false,
        };

    private static bool JoinsUnder(EntityEntry entry, IForeignKey foreignKey, Guid parent, IReadOnlyDictionary<object, PropertyValues?> storedRows) =>
        entry.State switch
        {
            EntityState.Added => Equals(entry.Property(foreignKey.Properties[0].Name).CurrentValue, parent),
            EntityState.Modified => Equals(entry.Property(foreignKey.Properties[0].Name).CurrentValue, parent) && !Equals(StoredParentOf(entry, foreignKey, storedRows), parent),
            _ => false,
        };

    private static object? StoredParentOf(EntityEntry entry, IForeignKey foreignKey, IReadOnlyDictionary<object, PropertyValues?> storedRows)
    {
        var column = foreignKey.Properties[0].Name;
        return storedRows.TryGetValue(entry.Entity, out var stored) && stored is not null ? stored[column] : entry.Property(column).OriginalValue;
    }

    private static async Task<int> CountStoredChildrenAsync<TRow>(ApplicationDbContext context, IProperty column, Guid parent, bool synchronously, CancellationToken cancellationToken)
        where TRow : class
    {
        var row = System.Linq.Expressions.Expression.Parameter(typeof(TRow), "row");
        var value = System.Linq.Expressions.Expression.Call(typeof(EF), nameof(EF.Property), [column.ClrType], row, System.Linq.Expressions.Expression.Constant(column.Name));
        var named = System.Linq.Expressions.Expression.Equal(value, System.Linq.Expressions.Expression.Constant(column.ClrType == typeof(Guid) ? parent : (Guid?)parent, column.ClrType));
        var query = context.Set<TRow>().IgnoreQueryFilters().Where(System.Linq.Expressions.Expression.Lambda<Func<TRow, bool>>(named, row));
        return synchronously ? query.Count() : await query.CountAsync(cancellationToken);
    }

    private static INavigation? DeclaredCollection(IForeignKey foreignKey)
    {
        if (foreignKey.PrincipalToDependent is not { IsCollection: true } collection
            || !typeof(IStateEntity).IsAssignableFrom(foreignKey.PrincipalEntityType.ClrType)
            || !WorkflowRegistry.HasWorkflow(foreignKey.PrincipalEntityType.ClrType)
            || !WorkflowRegistry.Default.Get(foreignKey.PrincipalEntityType.ClrType).DeclaresField(collection.Name))
        {
            return null;
        }

        return foreignKey.Properties is [{ } column] && (Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType) == typeof(Guid)
            && foreignKey.PrincipalKey.Properties is [{ Name: nameof(IStateEntity.Id) }]
            ? collection
            : throw new NotSupportedException($"Field enforcement judges a declared collection by a single Guid foreign key to its parent's Id; {foreignKey} is not one.");
    }

    private static IEnumerable<Guid> ParentsOf(EntityEntry entry, IForeignKey foreignKey, IReadOnlyDictionary<object, PropertyValues?> storedRows)
    {
        var column = entry.Property(foreignKey.Properties[0].Name);
        var parents = new HashSet<Guid>();
        if (entry.State != EntityState.Deleted && column.CurrentValue is Guid current)
        {
            parents.Add(current);
        }

        if (entry.State != EntityState.Added)
        {
            var before = storedRows.TryGetValue(entry.Entity, out var stored) && stored is not null ? stored[column.Metadata.Name] : column.OriginalValue;
            if (before is Guid previous)
            {
                parents.Add(previous);
            }
        }

        parents.Remove(Guid.Empty);
        return parents;
    }

    private static bool WritesAnything(EntityEntry entry, IReadOnlyDictionary<object, PropertyValues?> storedRows)
    {
        if (entry.State != EntityState.Modified)
        {
            return true;
        }

        var stored = storedRows.GetValueOrDefault(entry.Entity);
        return entry.Properties.Any(property => property.IsModified
            && !notWorkflowFields.Contains(property.Metadata.Name)
            && !property.Metadata.GetValueComparer().Equals(stored is null ? property.OriginalValue : stored[property.Metadata.Name], property.CurrentValue));
    }

    private WorkflowFieldEditException RefuseChildWrite(EntityEntry child, INavigation collection, Guid parent, Enum parentState, string reason, bool heldByTransition)
    {
        var (operation, entity, key) = Describe(child);
        var parentType = collection.DeclaringEntityType.ClrType.Name;
        IsolationLog().LogWarning("Refused {Operation} on {EntityType} {Key} in {ParentType} {ParentKey}.{Collection}: {Reason}", operation, entity, key, parentType, parent, collection.Name, reason);
        return new WorkflowFieldEditException(parentType, parent.ToString(), parentState, [collection.Name], $"Refused to {operation} {entity} {key} in {parentType} {parent}: {reason}", heldByTransition);
    }

    private static async Task<List<StoredParent>> ReadStoredParentsAsync<TRow, TState>(ApplicationDbContext context, List<Guid> keys, bool synchronously, CancellationToken cancellationToken)
        where TRow : class
        where TState : struct, Enum
    {
        var query = context.Set<TRow>().IgnoreQueryFilters().AsNoTracking()
            .Where(row => keys.Contains(EF.Property<Guid>(row, nameof(IStateEntity.Id))))
            .Select(row => new
            {
                Key = EF.Property<Guid>(row, nameof(IStateEntity.Id)),
                State = EF.Property<TState>(row, nameof(IStateEntity<>.State)),
                IsInTransition = EF.Property<bool>(row, nameof(IStateEntity.IsInTransition)),
            });
        var rows = synchronously ? query.ToList() : await query.ToListAsync(cancellationToken);
        return rows.Select(row => new StoredParent(row.Key, row.State, row.IsInTransition)).ToList();
    }

    private sealed record StoredParent(Guid Key, Enum State, bool IsInTransition);

    internal WorkflowFieldEditException RefuseBulkWorkflowWrite(string statement, string entity, string reason)
    {
        IsolationLog().LogWarning("Refused a bulk {Statement} of {EntityType}: {Reason}", statement, entity, reason);
        return new WorkflowFieldEditException(entity, string.Empty, null, [], $"Refused a bulk {statement} of {entity}: {reason}");
    }

    private void EndTransitionSaveScope(IReadOnlyList<IStateEntity> entities, string reason)
    {
        foreach (var entity in entities)
        {
            var depth = this.transitionSaveScopes.GetValueOrDefault(entity);
            if (depth <= 1)
            {
                this.transitionSaveScopes.Remove(entity);
            }
            else
            {
                this.transitionSaveScopes[entity] = depth - 1;
            }
        }

        IsolationLog().LogInformation("Transition save scope closed for {Reason}.", reason);
    }

    private sealed class TransitionSaveScope : IDisposable
    {
        private bool disposed;
        private readonly ApplicationDbContext context;
        private readonly IReadOnlyList<IStateEntity> entities;
        private readonly string reason;

        public TransitionSaveScope(ApplicationDbContext context, IReadOnlyList<IStateEntity> entities, string reason)
        {
            this.context = context;
            this.entities = entities;
            this.reason = reason;
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                this.context.EndTransitionSaveScope(this.entities, this.reason);
            }
        }
    }
}
