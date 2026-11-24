using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core.Entities.Organization;
using Staffora.Data.Configurations.Organization;

namespace Staffora.Test.Unit.Data;

public class OrganizationUnitModelTests
{
    private static IEntityType Unit => ModelUnderTest.Model.FindEntityType(typeof(OrganizationUnit))!;

    [Fact]
    public void A_unit_has_one_parent_column_so_a_second_parent_is_not_representable()
    {
        var parentKeys = Unit.GetForeignKeys().Where(key => key.PrincipalEntityType.ClrType == typeof(OrganizationUnit)).ToList();
        var parentKey = Assert.Single(parentKeys);
        var column = Assert.Single(parentKey.Properties);
        Assert.Equal((nameof(OrganizationUnit.ParentId), typeof(Guid?), DeleteBehavior.NoAction), (column.Name, column.ClrType, parentKey.DeleteBehavior));
        Assert.True(parentKey.IsUnique is false, "A unit may have many children but only one parent.");

        Assert.DoesNotContain(ModelUnderTest.Model.GetEntityTypes(), entityType =>
            entityType.ClrType != typeof(OrganizationUnit)
            && entityType.GetForeignKeys().Count(key => key.PrincipalEntityType.ClrType == typeof(OrganizationUnit)) > 1);
    }

    [Fact]
    public void Sibling_names_are_kept_apart_by_the_named_unique_index()
    {
        var index = Assert.Single(Unit.GetIndexes(), index => index.GetDatabaseName() == OrganizationUnitConfiguration.SiblingNameIndex);
        Assert.True(index.IsUnique);
        Assert.Equal([nameof(OrganizationUnit.TenantId), nameof(OrganizationUnit.ParentId), nameof(OrganizationUnit.Name)], index.Properties.Select(property => property.Name));
    }
}
