using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Data.Configurations.EntityAttributes;

namespace Staffora.Data.Configurations.Organization;

public class OrganizationUnitAttributeDefinitionConfiguration : IEntityTypeConfiguration<OrganizationUnitAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<OrganizationUnitAttributeDefinition> builder)
    {
        builder.ConfigureAttributeDefinition<OrganizationUnit, OrganizationUnitAttributeDefinition>();
    }
}

public class OrganizationUnitAttributeConfiguration : IEntityTypeConfiguration<OrganizationUnitAttribute>
{
    public void Configure(EntityTypeBuilder<OrganizationUnitAttribute> builder)
    {
        builder.ConfigureEntityAttribute<OrganizationUnit, OrganizationUnitAttributeDefinition, OrganizationUnitAttribute>();
        builder.HasOne(a => a.Entity)
            .WithMany(e => e.Attributes)
            .HasForeignKey(a => a.EntityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
