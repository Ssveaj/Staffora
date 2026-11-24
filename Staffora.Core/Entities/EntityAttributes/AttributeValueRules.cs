using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Staffora.Core.Entities.EntityAttributes;

public static class AttributeValueRules
{
    public const int TextMaxLength = 4000;

    public const string EmptyRow = "A custom field value that holds nothing is not stored. Clear the field by deleting its value.";

    public const string UnknownField = "A custom field value names a field that does not apply here.";

    public const string DuplicateField = "A custom field was sent twice.";

    public static string FieldNotInUse(string name) => $"{name} is not in use, so it takes no value.";

    public static string? DescribeInvalid(IEntityAttributeDefinition definition, IEntityAttributeValue value, IEntityAttributeValue? stored = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);
        var name = definition.Name;
        var type = definition.ValueType;

        if (!Enum.IsDefined(type))
        {
            return AttributeValues.IsEmpty(value) ? null : $"{name} has a type that takes no values.";
        }

        if (AttributeValues.HoldsOtherColumns(value, type))
        {
            return $"{name} holds {Holds(type)} only.";
        }

        switch (type)
        {
            case AttributeValueType.Text when value.StringValue?.Length > TextMaxLength:
                return $"{name} holds at most {TextMaxLength} characters.";

            case AttributeValueType.Number when value.DoubleValue is { } number && !double.IsFinite(number):
                return $"{name} holds a finite number.";

            case AttributeValueType.Choice when value.StringValue is { } key:
                var option = definition.Options.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
                if (option is null)
                {
                    return $"{name} has no such option.";
                }

                if (option.IsRetired && !string.Equals(stored?.StringValue, key, StringComparison.Ordinal))
                {
                    return $"{name}'s option \"{option.Label}\" is retired. Choose another.";
                }

                break;
        }

        return null;
    }

    public static string? DescribeInvalidValues<TValue>(IEnumerable<TValue> values, IReadOnlyDictionary<Guid, IEntityAttributeDefinition> definitions, IReadOnlyDictionary<Guid, TValue>? stored = null)
        where TValue : IEntityAttributeValue
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(definitions);
        var seen = new HashSet<Guid>();
        foreach (var value in values)
        {
            if (!definitions.TryGetValue(value.EntityAttributeDefinitionId, out var definition))
            {
                return UnknownField;
            }

            if (!seen.Add(value.EntityAttributeDefinitionId))
            {
                return DuplicateField;
            }

            var before = stored is not null && stored.TryGetValue(value.EntityAttributeDefinitionId, out var row) ? (IEntityAttributeValue)row : null;
            if (definition.State != CustomFieldState.Active)
            {
                if (before is not null && AttributeValues.HaveSameValues(value, before))
                {
                    continue;
                }

                return FieldNotInUse(definition.Name);
            }

            if (DescribeInvalid(definition, value, before) is { } reason)
            {
                return reason;
            }
        }

        return null;
    }

    public static string TypeLabel(AttributeValueType type) =>
        typeof(AttributeValueType).GetField(type.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? type.ToString();

    private static string Holds(AttributeValueType type) => type switch
    {
        AttributeValueType.Integer => "whole numbers",
        AttributeValueType.Number => "numbers",
        AttributeValueType.Date => "dates",
        AttributeValueType.Text => "text",
        AttributeValueType.Boolean => "yes or no",
        _ => "one of its options",
    };
}

public static class AttributeValues
{
    public static void Normalize(IEntityAttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.StringValue is not null && string.IsNullOrWhiteSpace(value.StringValue))
        {
            value.StringValue = null;
        }
    }

    public static bool IsBlank(IEntityAttributeValue value) =>
        string.IsNullOrWhiteSpace(value.StringValue) && value.Int32Value is null && value.DoubleValue is null && value.BooleanValue is null && value.DateValue is null;

    public static bool Fills(IEntityAttributeValue value, IEntityAttributeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(definition);
        if (IsBlank(value))
        {
            return false;
        }

        return definition.ValueType != AttributeValueType.Choice
            || definition.Options.Any(option => string.Equals(option.Key, value.StringValue, StringComparison.Ordinal));
    }

    public static bool IsEmpty(IEntityAttributeValue value) =>
        value.StringValue is null && value.Int32Value is null && value.DoubleValue is null && value.BooleanValue is null && value.DateValue is null;

    public static bool HoldsOtherColumns(IEntityAttributeValue value, AttributeValueType type) =>
        (value.StringValue is not null && type is not (AttributeValueType.Text or AttributeValueType.Choice))
        || (value.Int32Value is not null && type != AttributeValueType.Integer)
        || (value.DoubleValue is not null && type != AttributeValueType.Number)
        || (value.BooleanValue is not null && type != AttributeValueType.Boolean)
        || (value.DateValue is not null && type != AttributeValueType.Date);

    public static void CopyValues(IEntityAttributeValue source, IEntityAttributeValue target)
    {
        target.StringValue = source.StringValue;
        target.Int32Value = source.Int32Value;
        target.DoubleValue = source.DoubleValue;
        target.BooleanValue = source.BooleanValue;
        target.DateValue = source.DateValue;
    }

    public static string DisplayText(IEntityAttributeValue? value, IEntityAttributeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (value is null || IsBlank(value))
        {
            return "—";
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return definition.ValueType switch
        {
            AttributeValueType.Integer => value.Int32Value?.ToString(invariant) ?? "—",
            AttributeValueType.Number => value.DoubleValue?.ToString(invariant) ?? "—",
            AttributeValueType.Date => value.DateValue?.ToString("yyyy-MM-dd", invariant) ?? "—",
            AttributeValueType.Boolean => value.BooleanValue is { } yes ? (yes ? "Yes" : "No") : "—",
            AttributeValueType.Choice => definition.Options.FirstOrDefault(option => string.Equals(option.Key, value.StringValue, StringComparison.Ordinal)) is { } chosen
                ? chosen.IsRetired ? $"{chosen.Label} (retired)" : chosen.Label
                : "Unknown option",
            _ => value.StringValue ?? "—",
        };
    }

    public static bool HaveSameValues(IEntityAttributeValue first, IEntityAttributeValue second) =>
        first.StringValue == second.StringValue
        && first.Int32Value == second.Int32Value
        && Nullable.Equals(first.DoubleValue, second.DoubleValue)
        && first.BooleanValue == second.BooleanValue
        && first.DateValue == second.DateValue;
}
