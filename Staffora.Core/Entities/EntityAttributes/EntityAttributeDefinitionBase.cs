using Staffora.Core.Workflow;

namespace Staffora.Core.Entities.EntityAttributes;

public abstract class EntityAttributeDefinitionBase<TEntity> : StateEntityBase<CustomFieldState>, IEntityAttributeDefinition where TEntity : class
{
    public string Name { get; set; } = string.Empty;
    public AttributeValueType ValueType { get; set; }
    public Guid? TenantId { get; set; }
    public List<AttributeChoiceOption> Options { get; set; } = [];
    public bool IsRequired { get; set; }
    public DateTime? Modified { get; set; }
    public Guid? ModifiedById { get; set; }
    public Guid? ImpersonatedById { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
