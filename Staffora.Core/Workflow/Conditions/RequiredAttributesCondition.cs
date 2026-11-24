using System.Linq.Expressions;
using Staffora.Core.Entities.EntityAttributes;

namespace Staffora.Core.Workflow.Conditions;

public sealed class RequiredAttributesCondition<TState, TEntity, TValue> : Condition<TState, TEntity>, IRequiredCustomFieldsCondition
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
    where TValue : class, IEntityAttributeValue
{
    public const string Requirement = "Every required custom field has a value";

    private readonly Func<TEntity, IEnumerable<TValue>?> values;

    public RequiredAttributesCondition(Expression<Func<TEntity, IEnumerable<TValue>?>> values)
        : base(Requirement, [MemberPaths.PropertyOf(values).Name])
    {
        var property = MemberPaths.PropertyOf(values);
        var collection = CustomFieldCollection.Of(typeof(TEntity)).FirstOrDefault(candidate => candidate.Navigation.Name == property.Name)
            ?? throw new ArgumentException($"{typeof(TEntity).Name}.{property.Name} is not a collection of {typeof(TEntity).Name}'s own custom-field values.", nameof(values));
        DefinitionType = collection.DefinitionType;
        this.values = values.Compile();
    }

    public Type DefinitionType { get; }

    public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<TState, TEntity> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var unloaded = Reads.Where(path => !context.IsNavigationLoaded(path)).ToList();
        if (unloaded.Count > 0)
        {
            return [$"{Description} (cannot be checked: {string.Join(", ", unloaded)} not loaded)"];
        }

        if (context.Environment.CustomFields?.FieldsOf(DefinitionType) is not { } fields)
        {
            return [$"{Description} (cannot be checked: the custom fields were not read)"];
        }

        var missing = Missing(context.Entity, fields);
        return missing.Count == 0 ? [] : [Describe(missing)];
    }


    protected override bool IsMet(WorkflowContext<TState, TEntity> context) => GetBlockingReasons(context).Count == 0;

    private List<string> Missing(TEntity entity, IReadOnlyList<IEntityAttributeDefinition> fields)
    {
        var values = (this.values(entity) ?? []).Where(value => value is not null).ToList();
        return [.. fields
            .Where(field => field.IsRequired && Enum.IsDefined(field.ValueType))
            .Where(field => !values.Any(value => value.EntityAttributeDefinitionId == field.Id && AttributeValues.Fills(value, field)))
            .Select(field => field.Name)];
    }

    private static string Describe(List<string> names) => names.Count == 1
        ? $"{names[0]} is required"
        : $"{string.Join(", ", names[..^1])} and {names[^1]} are required";
}
