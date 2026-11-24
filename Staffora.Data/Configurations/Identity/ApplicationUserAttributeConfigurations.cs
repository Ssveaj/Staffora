using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Data.Configurations.EntityAttributes;

namespace Staffora.Data.Configurations.Identity;

public class ApplicationUserAttributeDefinitionConfiguration : IEntityTypeConfiguration<ApplicationUserAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<ApplicationUserAttributeDefinition> builder)
    {
        builder.ConfigureAttributeDefinition<ApplicationUser, ApplicationUserAttributeDefinition>();
    }
}

public class ApplicationUserAttributeConfiguration : IEntityTypeConfiguration<ApplicationUserAttribute>
{
    public void Configure(EntityTypeBuilder<ApplicationUserAttribute> builder)
    {
        builder.ConfigureEntityAttribute<ApplicationUser, ApplicationUserAttributeDefinition, ApplicationUserAttribute>();
        builder.HasOne(a => a.Entity)
            .WithMany(e => e.Attributes)
            .HasForeignKey(a => a.EntityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
