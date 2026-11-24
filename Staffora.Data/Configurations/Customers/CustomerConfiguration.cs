using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Customers;

namespace Staffora.Data.Configurations.Customers;

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public const string UniqueNameIndex = "IX_Customers_TenantId_Name";

    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(customer => customer.Name).HasMaxLength(Customer.NameMaxLength).IsRequired();
        builder.Property(customer => customer.Description).HasMaxLength(Customer.DescriptionMaxLength);
        builder.HasIndex(customer => new { customer.TenantId, customer.Name })
            .IsUnique()
            .HasDatabaseName(UniqueNameIndex);
        builder.HasIndex(customer => customer.LinkedTenantId)
            .IsUnique()
            .HasFilter($"[{nameof(Customer.LinkedTenantId)}] IS NOT NULL");
        builder.HasOne(customer => customer.DefaultUnit)
            .WithMany()
            .HasForeignKey(customer => customer.DefaultUnitId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
