namespace Staffora.Core.Entities.EntityAttributes;

public abstract class EntityAttributeBase<TEntity, TDefinition> : IEntityAttributeValue where TEntity : class where TDefinition : EntityAttributeDefinitionBase<TEntity>
{
    public Guid EntityId { get; set; }
    public Guid EntityAttributeDefinitionId { get; set; }
    public string? StringValue { get; set; }
    public int? Int32Value { get; set; }
    public double? DoubleValue { get; set; }
    public bool? BooleanValue { get; set; }
    public DateOnly? DateValue { get; set; }
    public TDefinition Definition { get; set; } = null!;
    public TEntity Entity { get; set; } = null!;
}
