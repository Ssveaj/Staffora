using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;

namespace Staffora.Test.Unit.Data;

public class CustomFieldDefinitionModelTests
{
    [Theory]
    [InlineData(typeof(ApplicationUserAttributeDefinition))]
    [InlineData(typeof(OrganizationUnitAttributeDefinition))]
    public void Whether_a_field_is_required_defaults_to_no_on_every_definition_table(Type definition)
    {
        var property = ModelUnderTest.Model.FindEntityType(definition)!.FindProperty(nameof(IEntityAttributeDefinition.IsRequired))!;

        Assert.False(property.IsNullable);
        Assert.Equal(false, property.GetDefaultValue());
    }
}
