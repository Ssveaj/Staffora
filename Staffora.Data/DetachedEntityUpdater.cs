using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core.Entities;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Data;

public sealed class DetachedEntityUpdater<T>
    where T : class
{
    private readonly IEntityType rootType;
    private readonly IReadOnlyList<INavigation> collections;

    private readonly Dictionary<IProperty, Func<object?>?> excluded;

    private EntityEntry? rootEntry;

    private readonly List<(object Row, object? Source)> attached = [];

    private Snapshot root = null!;
    private Dictionary<string, Dictionary<RowKey, Snapshot>> children = null!;

    public DetachedEntityUpdater(T entity, DbContext context, DetachedEntityUpdaterOptions<T>? options = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        Entity = entity;
        this.rootType = context.Model.FindEntityType(typeof(T))
            ?? throw new ArgumentException($"{typeof(T).Name} is not an entity type of {context.GetType().Name}.", nameof(context));
        options ??= DetachedEntityUpdaterOptions<T>.RootOnly;
        this.collections = options.Collections.Select(DeclaredCollection).ToList();
        this.excluded = options.ExcludedColumns.ToDictionary(pair => ExcludedColumn(pair.Key), pair => pair.Value);

        if (KeyOf(this.rootType, entity).IsDefault)
        {
            throw new ArgumentException($"The {typeof(T).Name} is not saved yet; a new entity is created by sending it, not through an updater.", nameof(entity));
        }

        TakeSnapshot();
    }

    public T Entity { get; }

    public bool HasChanges() => Diff().Count > 0;

    public IReadOnlyList<string> GetChangedMembers() => Diff().SelectMany(change => change.Describe()).ToList();

    public void ApplyChanges(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.attached.Clear();
        var changes = Diff();
        if (changes.Count == 0)
        {
            return;
        }

        ThrowOnRemovedStateEntity(changes);
        if (Refusal(changes) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        Apply(context, changes);
    }

    private void Apply(DbContext context, List<Change> changes)
    {
        var root = this.rootEntry = Attach(context, this.rootType, this.root, EntityState.Unchanged, Entity);
        var rootChange = changes.FirstOrDefault(change => change.Navigation is null);
        foreach (var column in rootChange?.Columns ?? [])
        {
            Write(root, column, Read(column, Entity));
        }

        if (rootChange is null && this.rootType.FindProperty(nameof(ITenantEntity.Modified)) is { } modified)
        {
            root.Property(modified.Name).IsModified = true;
        }

        foreach (var change in changes.Where(change => change.Navigation is not null))
        {
            switch (change.Kind)
            {
                case ChangeKind.Added:
                    AttachNewChild(context, change.Navigation!, change.Row!);
                    break;
                case ChangeKind.Updated:
                    var child = Attach(context, change.Type, change.Before!, EntityState.Unchanged, change.Row!);
                    foreach (var column in change.Columns)
                    {
                        Write(child, column, Read(column, change.Row!));
                    }

                    break;
                case ChangeKind.Removed:
                    Attach(context, change.Type, change.Before!, EntityState.Deleted, source: null);
                    break;
            }
        }
    }

    public void AcceptChanges()
    {
        foreach (var (row, source) in this.attached.Where(attached => attached.Source is not null))
        {
            var type = this.rootType.Model.FindEntityType(row.GetType())!;
            foreach (var column in Columns(type))
            {
                column.PropertyInfo!.SetValue(source, Read(column, row));
            }
        }

        this.attached.Clear();
        TakeSnapshot();
    }

    public Task<Result> SaveAsync(DbContext context, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(context, acceptChanges: true, beforeSave: null, cancellationToken);

    public Task<Result> SaveInTransactionAsync(DbContext context, Action<DbContext>? beforeSave = null, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(context, acceptChanges: false, beforeSave, cancellationToken);

    public object? OriginalValue(string column) =>
        this.root.Values.TryGetValue(column, out var value) ? value : throw new ArgumentException($"{typeof(T).Name} has no mapped column {column}.", nameof(column));

    public IReadOnlyList<string> ColumnsChangedSinceSnapshot(PropertyValues stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return [.. Columns(this.rootType)
            .Where(column => !column.IsPrimaryKey() && !column.IsConcurrencyToken && !this.excluded.ContainsKey(column) && !auditColumns.Contains(column.Name))
            .Where(column => !column.GetValueComparer().Equals(this.root.Values[column.Name], stored[column.Name]))
            .Select(column => column.Name)];
    }

    private static readonly HashSet<string> auditColumns = new(StringComparer.Ordinal)
    {
        nameof(ITenantEntity.Created),
        nameof(ITenantEntity.CreatedById),
        nameof(ITenantEntity.Modified),
        nameof(ITenantEntity.ModifiedById),
        nameof(ITenantEntity.ImpersonatedById),
    };

    private async Task<Result> SaveCoreAsync(DbContext context, bool acceptChanges, Action<DbContext>? beforeSave, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.attached.Clear();
        var changes = Diff();
        if (changes.Count == 0)
        {
            return Result.Success(message: "Nothing to save.");
        }

        ThrowOnRemovedStateEntity(changes);
        if (Refusal(changes) is { } refusal)
        {
            return Result.Failure(refusal);
        }

        Apply(context, changes);
        try
        {
            if (!await TakeOwnedTokensFromDatabaseAsync(cancellationToken))
            {
                return Refused(context, $"The {typeof(T).Name} was not found.");
            }

            beforeSave?.Invoke(context);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Refused(context, $"The {typeof(T).Name} was changed or moved to another state by someone else after it was opened. Reload it and try again.");
        }
        catch (WorkflowFieldEditException fieldRefusal)
        {
            return Refused(context, fieldRefusal switch
            {
                { HeldByTransition: true } => $"The {fieldRefusal.EntityType} is being moved by a transition right now. Reload it once the transition has finished and try again.",
                _ when fieldRefusal.Fields.Any(field => field is nameof(IStateEntity<>.State) or nameof(IStateEntity.IsInTransition)) => $"The state of the {fieldRefusal.EntityType} changes only through a transition.",
                _ => FieldRefusal(context, fieldRefusal),
            });
        }
        catch (TenantHeldException held)
        {
            return Refused(context, held.Message);
        }
        catch (TenantIsolationException)
        {
            return Refused(context, $"The {typeof(T).Name} was not found.");
        }

        if (acceptChanges)
        {
            AcceptChanges();
        }

        return Result.Success();
    }

    private static string FieldRefusal(DbContext context, WorkflowFieldEditException refusal)
    {
        var type = context.Model.GetEntityTypes().FirstOrDefault(entityType => entityType.ClrType.Name == refusal.EntityType)?.ClrType;
        var state = refusal.PersistedState is { } persisted ? DisplayOf(persisted) : null;
        var reasons = new List<string>();
        var locked = refusal.Fields.Except(refusal.EmptiedRequiredFields).ToList();
        if (locked.Count > 0)
        {
            reasons.Add($"{string.Join(", ", locked.Select(field => LabelOf(type, field)))} cannot be changed while the {refusal.EntityType} is {state}.");
        }

        if (refusal.EmptiedRequiredFields.Count > 0)
        {
            var labels = refusal.EmptiedCustomFields.Count > 0 ? [.. refusal.EmptiedCustomFields] : refusal.EmptiedRequiredFields.Select(field => LabelOf(type, field)).ToList();
            reasons.Add($"{string.Join(", ", labels)} {(labels.Count == 1 ? "is" : "are")} required while the {refusal.EntityType} is {state}.");
        }

        return string.Join(" ", reasons);
    }

    private static string LabelOf(Type? type, string field) =>
        type?.GetProperty(field)?.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetName() ?? field;

    private static string DisplayOf(Enum value) =>
        value.GetType().GetField(value.ToString())?.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetName() ?? value.ToString();

    private async Task<bool> TakeOwnedTokensFromDatabaseAsync(CancellationToken cancellationToken)
    {
        var tokens = this.excluded.Where(pair => pair.Key.IsConcurrencyToken).ToList();
        if (tokens.Count == 0 || this.rootEntry is null)
        {
            return true;
        }

        var stored = await this.rootEntry.GetDatabaseValuesAsync(cancellationToken);
        if (stored is null)
        {
            return false;
        }

        foreach (var (column, renew) in tokens)
        {
            var property = this.rootEntry.Property(column.Name);
            property.OriginalValue = stored[column.Name];
            property.CurrentValue = renew is null ? stored[column.Name] : renew();
            property.IsModified = renew is not null;
        }

        return true;
    }

    private IProperty ExcludedColumn(string name) =>
        this.rootType.FindProperty(name) is { PropertyInfo: not null } column && !column.IsPrimaryKey()
            ? column
            : throw new ArgumentException($"{typeof(T).Name}.{name} is not a mapped column the updater could leave alone.", nameof(name));

    private Result Refused(DbContext context, string reason)
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(entry => this.attached.Any(attached => ReferenceEquals(attached.Row, entry.Entity))).ToList())
        {
            entry.State = EntityState.Detached;
        }

        this.attached.Clear();
        return Result.Failure(reason);
    }

    private static void ThrowOnRemovedStateEntity(List<Change> changes)
    {
        if (changes.FirstOrDefault(change => change.Kind == ChangeKind.Removed && typeof(IStateEntity).IsAssignableFrom(change.Type.ClrType)) is { } removedStateEntity)
        {
            throw new InvalidOperationException(
                $"{removedStateEntity.Type.ClrType.Name} {removedStateEntity.Key} is a state entity: it is deleted through its own workflow (ADR 0016), never by its parent's updater.");
        }
    }

    private string? Refusal(List<Change> changes)
    {
        foreach (var navigation in this.collections)
        {
            var foreignKey = navigation.ForeignKey;
            foreach (var child in Items(navigation))
            {
                var movedOut = Enumerable.Range(0, foreignKey.Properties.Count).Any(i =>
                    foreignKey.Properties[i].PropertyInfo is { } column
                    && column.GetValue(child) is { } value
                    && !IsDefault(value, foreignKey.Properties[i].ClrType)
                    && !Equals(value, RootValue(foreignKey.PrincipalKey.Properties[i])));
                if (movedOut)
                {
                    return $"{navigation.Name}[{ChildKey(navigation, child)}] names another {typeof(T).Name}; a child is never moved through its parent.";
                }
            }

            if (changes.FirstOrDefault(change => change.Navigation == navigation && change.Kind == ChangeKind.Updated && change.Columns.Any(foreignKey.Properties.Contains)) is { } cleared)
            {
                return $"{navigation.Name}[{cleared.Key}] names another {typeof(T).Name}; a child is never moved through its parent.";
            }
        }

        if (!typeof(IStateEntity).IsAssignableFrom(this.rootType.ClrType))
        {
            return null;
        }

        var state = (Enum)this.root.Values[nameof(IStateEntity<>.State)]!;
        var workflow = WorkflowRegistry.Default.Get(this.rootType.ClrType);
        var locked = changes
            .Where(change => change.Navigation is not null)
            .Select(change => change.Navigation!.Name)
            .Distinct()
            .Where(name => !workflow.GetFieldAccess(state, name).HasFlag(FieldAccessFlags.Editable))
            .ToList();

        return locked.Count == 0 ? null : $"{string.Join(", ", locked.Select(name => LabelOf(typeof(T), name)))} cannot be changed while the {typeof(T).Name} is {state}.";
    }

    private object? RootValue(IProperty column) => this.root.Values[column.Name];

    private INavigation DeclaredCollection(string name)
    {
        var navigation = this.rootType.FindNavigation(name);
        if (navigation is not { IsCollection: true, PropertyInfo: not null })
        {
            throw new ArgumentException(this.rootType.FindSkipNavigation(name) is not null
                ? $"{typeof(T).Name}.{name} is many-to-many; its edges change only through dedicated service commands (ADR 0015)."
                : $"{typeof(T).Name}.{name} is not a one-to-many collection navigation.", nameof(name));
        }

        return navigation;
    }

    private void TakeSnapshot()
    {
        this.root = Snapshot.Of(this.rootType, Entity);
        this.children = [];
        foreach (var navigation in this.collections)
        {
            var children = new Dictionary<RowKey, Snapshot>();
            foreach (var child in Items(navigation))
            {
                if (ChildKey(navigation, child) is { IsDefault: false } key)
                {
                    children.TryAdd(key, Snapshot.Of(navigation.TargetEntityType, child));
                }
            }

            this.children[navigation.Name] = children;
        }
    }

    private List<Change> Diff()
    {
        var changes = new List<Change>();
        var rootColumns = this.root.ChangedColumns(Entity).Where(column => !this.excluded.ContainsKey(column)).ToList();
        if (rootColumns.Count > 0)
        {
            changes.Add(new Change(ChangeKind.Updated, null, this.rootType, this.root.Key, Entity, this.root, rootColumns));
        }

        foreach (var navigation in this.collections)
        {
            var before = this.children[navigation.Name];
            var type = navigation.TargetEntityType;
            var seen = new HashSet<RowKey>();
            foreach (var child in Items(navigation))
            {
                var key = ChildKey(navigation, child);
                if (key.IsDefault || !before.TryGetValue(key, out var snapshot))
                {
                    if (key.IsDefault || seen.Add(key))
                    {
                        changes.Add(new Change(ChangeKind.Added, navigation, type, key, child, null, []));
                    }
                }
                else if (seen.Add(key) && snapshot.ChangedColumns(child) is { Count: > 0 } columns)
                {
                    changes.Add(new Change(ChangeKind.Updated, navigation, type, key, child, snapshot, columns));
                }
            }

            changes.AddRange(before.Where(pair => !seen.Contains(pair.Key))
                .Select(pair => new Change(ChangeKind.Removed, navigation, type, pair.Key, null, pair.Value, [])));
        }

        return changes;
    }

    private EntityEntry Attach(DbContext context, IEntityType type, Snapshot snapshot, EntityState state, object? source)
    {
        var keyColumns = type.FindPrimaryKey()!.Properties;
        if (context.ChangeTracker.Entries().Any(entry => entry.Metadata.ClrType == type.ClrType && snapshot.Key.Equals(new RowKey(keyColumns.Select(column => entry.Property(column.Name).CurrentValue).ToArray()))))
        {
            throw new InvalidOperationException($"The context already tracks {type.ClrType.Name} {snapshot.Key}; apply an updater to a fresh context.");
        }

        var row = NewRow(type);
        foreach (var column in Columns(type))
        {
            column.PropertyInfo!.SetValue(row, snapshot.Values[column.Name]);
        }

        var entry = context.Entry(row);
        entry.State = state;
        this.attached.Add((row, source));
        return entry;
    }

    private void AttachNewChild(DbContext context, INavigation navigation, object child)
    {
        var type = navigation.TargetEntityType;
        var row = NewRow(type);
        foreach (var column in Columns(type))
        {
            column.PropertyInfo!.SetValue(row, Read(column, child));
        }

        var foreignKey = navigation.ForeignKey;
        for (var i = 0; i < foreignKey.Properties.Count; i++)
        {
            foreignKey.Properties[i].PropertyInfo!.SetValue(row, RootValue(foreignKey.PrincipalKey.Properties[i]));
        }

        context.Add(row);
        this.attached.Add((row, child));
    }

    private RowKey ChildKey(INavigation navigation, object child)
    {
        var foreignKey = navigation.ForeignKey;
        var values = navigation.TargetEntityType.FindPrimaryKey()!.Properties.Select(column =>
        {
            var value = Read(column, child);
            var index = IndexOf(foreignKey.Properties, column);
            return index >= 0 && IsDefault(value, column.ClrType) ? RootValue(foreignKey.PrincipalKey.Properties[index]) : value;
        });

        return new RowKey(values.ToArray());
    }

    private IEnumerable<object> Items(INavigation navigation) =>
        ((IEnumerable?)navigation.PropertyInfo!.GetValue(Entity))?.Cast<object>().Where(item => item is not null) ?? [];

    private static void Write(EntityEntry entry, IProperty column, object? value)
    {
        var property = entry.Property(column.Name);
        property.CurrentValue = value;
        property.IsModified = true;
    }

    private static RowKey KeyOf(IEntityType type, object row) =>
        new(type.FindPrimaryKey()!.Properties.Select(column => Read(column, row)).ToArray());

    private static IEnumerable<IProperty> Columns(IEntityType type) =>
        type.GetProperties().Where(column => column.PropertyInfo is not null);

    private static object? Read(IProperty column, object row) => column.PropertyInfo!.GetValue(row);

    private static object NewRow(IEntityType type) => Activator.CreateInstance(type.ClrType, nonPublic: true)!;

    private static int IndexOf(IReadOnlyList<IProperty> properties, IProperty property)
    {
        for (var i = 0; i < properties.Count; i++)
        {
            if (properties[i] == property)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsDefault(object? value, Type type) =>
        value is null || (type.IsValueType && value.Equals(Activator.CreateInstance(type)));

    private enum ChangeKind
    {
        Added,
        Updated,
        Removed,
    }

    private sealed record Change(ChangeKind Kind, INavigation? Navigation, IEntityType Type, RowKey Key, object? Row, Snapshot? Before, IReadOnlyList<IProperty> Columns)
    {
        public IEnumerable<string> Describe()
        {
            var row = Navigation is null ? null : $"{Navigation.Name}[{(Key.IsDefault ? "new" : Key.ToString())}]";
            return Kind switch
            {
                ChangeKind.Added => [$"{row} added"],
                ChangeKind.Removed => [$"{row} removed"],
                _ => Columns.Select(column => row is null ? column.Name : $"{row}.{column.Name}"),
            };
        }
    }

    private sealed class Snapshot
    {
        private readonly IEntityType type;

        private Snapshot(IEntityType type, RowKey key, Dictionary<string, object?> values)
        {
            this.type = type;
            Key = key;
            Values = values;
        }

        public RowKey Key { get; }

        public IReadOnlyDictionary<string, object?> Values { get; }

        public static Snapshot Of(IEntityType type, object row) =>
            new(type, KeyOf(type, row), Columns(type).ToDictionary(column => column.Name, column => column.GetValueComparer().Snapshot(Read(column, row))));

        public List<IProperty> ChangedColumns(object row) =>
            Columns(this.type).Where(column => !column.IsPrimaryKey() && !column.GetValueComparer().Equals(Values[column.Name], Read(column, row))).ToList();
    }

    private sealed class RowKey : IEquatable<RowKey>
    {
        private readonly object?[] values;

        public RowKey(object?[] values)
        {
            this.values = values;
        }

        public bool IsDefault => this.values.All(value => value is null || (value.GetType().IsValueType && value.Equals(Activator.CreateInstance(value.GetType()))));

        public bool Equals(RowKey? other) => other is not null && this.values.SequenceEqual(other.values);

        public override bool Equals(object? obj) => Equals(obj as RowKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in this.values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }

        public override string ToString() => string.Join("|", this.values);
    }
}
