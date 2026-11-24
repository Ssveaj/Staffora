using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data.Configurations.EntityAttributes;

public static class EntityAttributeDefinitionBaseConfiguration
{
    public static void ConfigureAttributeDefinition<TEntity, TDefinition>(
        this EntityTypeBuilder<TDefinition> builder)
        where TEntity : class
        where TDefinition : EntityAttributeDefinitionBase<TEntity>
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).HasMaxLength(AttributeDefinitionRules.NameMaxLength).IsRequired();
        builder.Property(d => d.Options)
            .HasConversion(ChoiceOptionsConversion.Converter, ChoiceOptionsConversion.Comparer)
            .IsRequired();

        builder.Property(d => d.IsRequired).IsRequired().HasDefaultValue(false);
        builder.Property(d => d.RowVersion).IsRowVersion();

        builder.Property(d => d.TenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(d => new { d.TenantId, d.Name })
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName(NameIndexOf(typeof(TDefinition)));

        builder.Property(d => d.Created).IsRequired();
        builder.Property(d => d.CreatedById).IsRequired();
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(d => d.CreatedById)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(d => d.ModifiedById)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(d => d.ImpersonatedById)
            .OnDelete(DeleteBehavior.NoAction);
    }

    public static string NameIndexOf(Type definitionType) => $"IX_{definitionType.Name}s_TenantId_Name";
}
