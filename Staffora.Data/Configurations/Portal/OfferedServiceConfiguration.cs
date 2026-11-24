using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Portal;

namespace Staffora.Data.Configurations.Portal;

public class OfferedServiceConfiguration : IEntityTypeConfiguration<OfferedService>
{
    public void Configure(EntityTypeBuilder<OfferedService> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(copy => copy.Name).HasMaxLength(OfferedService.NameMaxLength).IsRequired();
        builder.Property(copy => copy.Description).HasMaxLength(OfferedService.DescriptionMaxLength);
        builder.Property(copy => copy.LinkedCrossTenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(copy => copy.Supplier)
            .WithMany()
            .HasForeignKey(copy => copy.SupplierId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(copy => new { copy.TenantId, copy.SupplierId, copy.LinkedCrossTenantId }).IsUnique();
        builder.ToTable(table => table.HasCheckConstraint("CK_OfferedServices_DefaultSlotCount", $"[{nameof(OfferedService.DefaultSlotCount)}] BETWEEN 1 AND 20"));
    }
}
