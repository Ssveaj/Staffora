using Staffora.Core.Workflow;

namespace Staffora.Core.Entities.EntityAttributes;

public interface IEntityAttributeDefinition : IStateEntity<CustomFieldState>
{
    string Name { get; set; }

    AttributeValueType ValueType { get; set; }

    Guid? TenantId { get; set; }

    List<AttributeChoiceOption> Options { get; set; }

    bool IsRequired { get; set; }

    DateTime Created { get; set; }

    Guid CreatedById { get; set; }

    DateTime? Modified { get; set; }

    Guid? ModifiedById { get; set; }

    Guid? ImpersonatedById { get; set; }
}
