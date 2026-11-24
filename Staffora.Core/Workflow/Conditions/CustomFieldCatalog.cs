using Staffora.Core.Entities.EntityAttributes;

namespace Staffora.Core.Workflow.Conditions;

public interface ICustomFieldCatalog
{
    IReadOnlyList<IEntityAttributeDefinition>? FieldsOf(Type definitionType);
}

public interface ICustomFieldCondition
{
    Type DefinitionType { get; }
}

public interface IRequiredCustomFieldsCondition : ICustomFieldCondition;

public sealed class CustomFieldCatalog : ICustomFieldCatalog
{
    private readonly Dictionary<Type, IReadOnlyList<IEntityAttributeDefinition>> fields;

    public CustomFieldCatalog(IReadOnlyDictionary<Type, IReadOnlyList<IEntityAttributeDefinition>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        this.fields = fields.ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    public static CustomFieldCatalog Of(Type definitionType, IEnumerable<IEntityAttributeDefinition> fields) =>
        new(new Dictionary<Type, IReadOnlyList<IEntityAttributeDefinition>> { [definitionType] = [.. fields] });

    public IReadOnlyList<IEntityAttributeDefinition>? FieldsOf(Type definitionType) => this.fields.GetValueOrDefault(definitionType);
}

public static class CustomFieldRequirements
{
    public static bool CanBeRequired(Type definitionType)
    {
        ArgumentNullException.ThrowIfNull(definitionType);
        for (var current = definitionType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(EntityAttributeDefinitionBase<>))
            {
                var entity = current.GetGenericArguments()[0];
                return WorkflowRegistry.HasWorkflow(entity) && WorkflowRegistry.Default.Get(entity).StepsRequiringCustomFields(definitionType).Count > 0;
            }
        }

        return false;
    }
}
