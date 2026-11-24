using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Portal;

namespace Staffora.Data.Configurations.Portal;

public class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(supplier => supplier.Name).HasMaxLength(Supplier.NameMaxLength).IsRequired();
        builder.Property(supplier => supplier.LinkedTenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasIndex(supplier => new { supplier.TenantId, supplier.LinkedTenantId }).IsUnique();
    }
}
