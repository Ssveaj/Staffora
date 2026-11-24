using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Data;

namespace Staffora.Services.CustomFields;

internal static class CustomFieldValues
{
    public static async Task<string?> DescribeInvalidAsync<TDefinition, TValue>(ApplicationDbContext context, Guid tenantId, IEnumerable<TValue> values, IEnumerable<TValue>? stored = null, CancellationToken cancellationToken = default)
        where TDefinition : class, IEntityAttributeDefinition
        where TValue : IEntityAttributeValue
    {
        var sent = values.ToList();
        if (sent.Count == 0)
        {
            return null;
        }

        var ids = sent.Select(value => value.EntityAttributeDefinitionId).Distinct().ToList();
        var definitions = await context.Set<TDefinition>().AsNoTracking()
            .Where(definition => ids.Contains(definition.Id) && (definition.TenantId == null || definition.TenantId == tenantId))
            .ToListAsync(cancellationToken);
        var storedById = (stored ?? []).GroupBy(value => value.EntityAttributeDefinitionId).ToDictionary(group => group.Key, group => group.First());
        return AttributeValueRules.DescribeInvalidValues(sent, definitions.ToDictionary(definition => definition.Id, definition => (IEntityAttributeDefinition)definition), storedById);
    }

    public static List<TValue> Normalized<TValue>(IEnumerable<TValue>? values)
        where TValue : IEntityAttributeValue
    {
        var list = (values ?? []).ToList();
        foreach (var value in list)
        {
            AttributeValues.Normalize(value);
        }

        return list;
    }

    public static void Merge<TValue>(ApplicationDbContext context, ICollection<TValue> stored, IEnumerable<TValue> values, Action<TValue> add)
        where TValue : class, IEntityAttributeValue
    {
        foreach (var value in values)
        {
            var row = stored.FirstOrDefault(candidate => candidate.EntityAttributeDefinitionId == value.EntityAttributeDefinitionId);
            if (row is not null && AttributeValues.IsEmpty(value))
            {
                context.Remove(row);
                stored.Remove(row);
            }
            else if (row is not null)
            {
                AttributeValues.CopyValues(value, row);
            }
            else if (!AttributeValues.IsEmpty(value))
            {
                add(value);
            }
        }
    }
}
