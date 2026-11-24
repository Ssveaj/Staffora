namespace Staffora.Core.Entities.EntityAttributes;

public static class AttributeDefinitionRules
{
    public const int NameMaxLength = 100;

    public const string RequiredYesOrNo = "A yes-or-no field cannot be required: a box left unticked reads as no.";

    public static IReadOnlyList<AttributeValueType> Types { get; } = Enum.GetValues<AttributeValueType>();

    public static string? DescribeInvalid(IEntityAttributeDefinition definition, IEntityAttributeDefinition? stored = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = definition.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return "A custom field needs a name.";
        }

        if (name.Length > NameMaxLength)
        {
            return $"A custom field's name has at most {NameMaxLength} characters.";
        }

        if (!Enum.IsDefined(definition.ValueType))
        {
            return "Choose the kind of value the field holds.";
        }

        if (definition.IsRequired && definition.ValueType == AttributeValueType.Boolean)
        {
            return RequiredYesOrNo;
        }

        if (stored is not null && stored.ValueType != definition.ValueType)
        {
            return $"A saved field keeps its type: {stored.Name} holds {AttributeValueRules.TypeLabel(stored.ValueType).ToLowerInvariant()} values. Add a new field for another kind of value.";
        }

        var options = definition.Options ?? [];
        if (definition.ValueType != AttributeValueType.Choice)
        {
            return options.Count == 0 ? null : "Only a choice field has options.";
        }

        return DescribeInvalidOptions(options, stored?.Options ?? []);
    }

    private static string? DescribeInvalidOptions(IReadOnlyList<AttributeChoiceOption> options, IReadOnlyList<AttributeChoiceOption> stored)
    {
        if (options.Count > AttributeChoiceOption.MaxOptions)
        {
            return $"A choice field has at most {AttributeChoiceOption.MaxOptions} options.";
        }

        if (!options.Any(option => !option.IsRetired))
        {
            return "A choice field needs at least one option that is not retired.";
        }

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            var label = option.Label?.Trim() ?? string.Empty;
            if (label.Length == 0)
            {
                return "Every option needs a label.";
            }

            if (label.Length > AttributeChoiceOption.LabelMaxLength)
            {
                return $"An option's label has at most {AttributeChoiceOption.LabelMaxLength} characters.";
            }

            if (!labels.Add(label))
            {
                return $"Two options are labelled \"{label}\".";
            }

            if (!AttributeChoiceOption.IsValidKey(option.Key))
            {
                return $"The option \"{label}\" has no valid key.";
            }

            if (!keys.Add(option.Key))
            {
                return $"Two options share the key \"{option.Key}\".";
            }
        }

        if (stored.FirstOrDefault(kept => !keys.Contains(kept.Key)) is { } removed)
        {
            return $"The option \"{removed.Label}\" cannot be removed, since values may hold it. Retire it instead.";
        }

        return null;
    }
}
