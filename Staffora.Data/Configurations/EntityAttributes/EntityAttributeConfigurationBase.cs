using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.EntityAttributes;

namespace Staffora.Data.Configurations.EntityAttributes;

public static class EntityAttributeConfigurationBase
{
    public static void ConfigureEntityAttribute<TEntity, TDefinition, TAttribute>(this EntityTypeBuilder<TAttribute> builder)
        where TEntity : class
        where TDefinition : EntityAttributeDefinitionBase<TEntity>
        where TAttribute : EntityAttributeBase<TEntity, TDefinition>
    {
        builder.HasKey(a => new { a.EntityId, a.EntityAttributeDefinitionId });
        builder.Property(a => a.StringValue).HasMaxLength(AttributeValueRules.TextMaxLength);
        builder.HasOne(a => a.Definition)
            .WithMany()
            .HasForeignKey(a => a.EntityAttributeDefinitionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
