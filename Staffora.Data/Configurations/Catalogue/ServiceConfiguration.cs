using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Catalogue;

namespace Staffora.Data.Configurations.Catalogue;

public class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public const string UniqueNameIndex = "IX_Services_TenantId_Name";

    public const string CountCheck = "CK_Services_DefaultSlotCount";

    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(service => service.Name).HasMaxLength(Service.NameMaxLength).IsRequired();
        builder.Property(service => service.Description).HasMaxLength(Service.DescriptionMaxLength);
        builder.Property(service => service.CrossTenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasIndex(service => new { service.TenantId, service.Name })
            .IsUnique()
            .HasDatabaseName(UniqueNameIndex);
        builder.HasIndex(service => service.CrossTenantId).IsUnique();
        builder.ToTable(table => table.HasCheckConstraint(CountCheck, $"[{nameof(Service.DefaultSlotCount)}] BETWEEN {Service.MinSlotCount} AND {Service.MaxSlotCount}"));
    }
}
