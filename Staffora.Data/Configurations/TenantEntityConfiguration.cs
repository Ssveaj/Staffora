using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Identity;

namespace Staffora.Data.Configurations;

public static class TenantEntityConfiguration
{
    public static EntityTypeBuilder<T> ConfigureTenantEntity<T>(this EntityTypeBuilder<T> builder)
        where T : class, ITenantEntity
    {
        builder.Property(e => e.TenantId).IsRequired().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(e => e.Created).IsRequired();
        builder.Property(e => e.CreatedById).IsRequired();

        builder.HasIndex(e => e.TenantId);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.CreatedById)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.ModifiedById)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.ImpersonatedById)
            .OnDelete(DeleteBehavior.NoAction);

        return builder;
    }
}
