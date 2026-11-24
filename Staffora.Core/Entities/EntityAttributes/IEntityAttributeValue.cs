namespace Staffora.Core.Entities.EntityAttributes;

public interface IEntityAttributeValue
{
    Guid EntityId { get; set; }

    Guid EntityAttributeDefinitionId { get; set; }

    string? StringValue { get; set; }

    int? Int32Value { get; set; }

    double? DoubleValue { get; set; }

    bool? BooleanValue { get; set; }

    DateOnly? DateValue { get; set; }
}
