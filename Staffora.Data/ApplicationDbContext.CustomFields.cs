using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Execution;

namespace Staffora.Data;

public partial class ApplicationDbContext
{
    private static readonly MethodInfo definitionsQuery =
        typeof(ApplicationDbContext).GetMethod(nameof(DefinitionsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private Guid? StampAttributeDefinition(EntityEntry entry, RuntimeSnapshot? session, bool systemScope, DateTime now)
    {
        var row = (IEntityAttributeDefinition)entry.Entity;
        var actor = session?.EffectiveUserId;
        switch (entry.State)
        {
            case EntityState.Added:
                if (!systemScope || row.Created == default)
                {
                    row.Created = now;
                }

                row.Modified = null;
                row.ModifiedById = null;
                if (systemScope && row.CreatedById != Guid.Empty)
                {
                    row.ImpersonatedById = null;
                }
                else
                {
                    row.CreatedById = actor ?? throw RefuseStamp(entry, "creator");
                    row.ImpersonatedById = session?.ImpersonatedById;
                }

                return row.CreatedById;

            case EntityState.Modified:
                RestoreInsertOnlyColumns(entry);
                row.Modified = now;

                var editor = entry.Property(nameof(IEntityAttributeDefinition.ModifiedById));
                var named = this.namedEditors?.Contains(row) == true || !Equals(editor.OriginalValue, editor.CurrentValue);
                if (!systemScope || row.ModifiedById is null || !named)
                {
                    row.ModifiedById = actor ?? throw RefuseStamp(entry, "editor");
                }

                return row.ModifiedById;

            default:
                return null;
        }
    }

    private async Task CheckCustomFieldsAsync(Dictionary<object, PropertyValues?> storedRows, bool synchronously, CancellationToken cancellationToken)
    {
        var pending = ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified).ToList();

        foreach (var entry in pending.Where(entry => IsAttributeDefinition(entry.Metadata.ClrType)))
        {
            var stored = entry.State == EntityState.Modified && storedRows.TryGetValue(entry.Entity, out var values) && values is not null
                ? (IEntityAttributeDefinition)values.ToObject()
                : null;
            if (AttributeDefinitionRules.DescribeInvalid((IEntityAttributeDefinition)entry.Entity, stored) is { } reason)
            {
                throw RefuseCustomField(entry, reason);
            }
        }

        var valueRows = pending.Where(entry => IsAttributeValue(entry.Metadata.ClrType)).ToList();
        foreach (var table in valueRows.GroupBy(entry => entry.Metadata.FindNavigation(nameof(EntityAttributeBase<,>.Definition))!.ForeignKey.PrincipalEntityType))
        {
            var definitions = await DefinitionsOfAsync(table.Key, table.Select(entry => ((IEntityAttributeValue)entry.Entity).EntityAttributeDefinitionId), synchronously, cancellationToken);
            foreach (var entry in table)
            {
                var value = (IEntityAttributeValue)entry.Entity;

                if (!definitions.TryGetValue(value.EntityAttributeDefinitionId, out var definition))
                {
                    throw RefuseCustomField(entry, AttributeValueRules.UnknownField);
                }

                if (AttributeValues.IsBlank(value))
                {
                    throw RefuseCustomField(entry, AttributeValueRules.EmptyRow);
                }

                if (definition.State != CustomFieldState.Active && (entry.State == EntityState.Added || ValueChanged(entry)))
                {
                    throw RefuseCustomField(entry, AttributeValueRules.FieldNotInUse(definition.Name));
                }

                var stored = entry.State == EntityState.Modified && HoldsRetiredOption(definition, value)
                    ? await StoredValueAsync(entry, synchronously, cancellationToken)
                    : null;
                if (AttributeValueRules.DescribeInvalid(definition, value, stored) is { } reason)
                {
                    throw RefuseCustomField(entry, reason);
                }
            }
        }
    }

    private async Task<Dictionary<Guid, IEntityAttributeDefinition>> DefinitionsOfAsync(IEntityType table, IEnumerable<Guid> ids, bool synchronously, CancellationToken cancellationToken)
    {
        var wanted = ids.ToHashSet();
        var found = ChangeTracker.Entries()
            .Where(entry => entry.Metadata.ClrType == table.ClrType && entry.State is EntityState.Added or EntityState.Modified)
            .Select(entry => (IEntityAttributeDefinition)entry.Entity)
            .Where(definition => wanted.Contains(definition.Id))
            .ToDictionary(definition => definition.Id);
        var missing = wanted.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var read = (Task<List<IEntityAttributeDefinition>>)definitionsQuery.MakeGenericMethod(table.ClrType).Invoke(null, [this, missing, synchronously, cancellationToken])!;
            foreach (var definition in await read)
            {
                found[definition.Id] = definition;
            }
        }

        return found;
    }

    private static bool ValueChanged(EntityEntry entry) =>
        entry.Properties.Any(column => column.Metadata.Name is nameof(IEntityAttributeValue.StringValue) or nameof(IEntityAttributeValue.Int32Value) or nameof(IEntityAttributeValue.DoubleValue) or nameof(IEntityAttributeValue.BooleanValue) or nameof(IEntityAttributeValue.DateValue)
            && column.IsModified
            && !Equals(column.OriginalValue, column.CurrentValue));

    private static bool HoldsRetiredOption(IEntityAttributeDefinition definition, IEntityAttributeValue value) =>
        definition.ValueType == AttributeValueType.Choice
        && value.StringValue is { } key
        && definition.Options.Any(option => option.IsRetired && string.Equals(option.Key, key, StringComparison.Ordinal));

    private static async Task<IEntityAttributeValue?> StoredValueAsync(EntityEntry entry, bool synchronously, CancellationToken cancellationToken)
    {
        var values = synchronously ? entry.GetDatabaseValues() : await entry.GetDatabaseValuesAsync(cancellationToken);
        return values?.ToObject() as IEntityAttributeValue;
    }

    private static async Task<List<IEntityAttributeDefinition>> DefinitionsAsync<TDefinition>(ApplicationDbContext context, List<Guid> ids, bool synchronously, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        var query = context.Set<TDefinition>().AsNoTracking().Where(definition => ids.Contains(definition.Id));
        var rows = synchronously ? query.ToList() : await query.ToListAsync(cancellationToken);
        return [.. rows];
    }

    private CustomFieldException RefuseCustomField(EntityEntry entry, string reason)
    {
        var (operation, entity, key) = Describe(entry);
        IsolationLog().LogWarning("Refused custom field {Operation} on {EntityType} {Key}: {Reason}", operation, entity, key, reason);
        return new CustomFieldException(reason);
    }

    internal TenantIsolationException RefuseBulkCustomFieldWrite(string statement, string entity, string reason)
    {
        IsolationLog().LogWarning("Refused a bulk {Statement} of {EntityType}: {Reason} Runtime tenant {RuntimeTenantId}.", statement, entity, reason, CurrentTenantId);
        return new TenantIsolationException($"Refused a bulk {statement} of {entity}: {reason}");
    }
}
