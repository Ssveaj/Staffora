using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Portal;

namespace Staffora.Data.Configurations.Portal;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(request => request.Name).HasMaxLength(Order.NameMaxLength).IsRequired();
        builder.Property(request => request.Description).HasMaxLength(Order.DescriptionMaxLength);
        builder.Property(request => request.PlaceName).HasMaxLength(Job.PlaceNameMaxLength);
        builder.Property(request => request.StreetAddress).HasMaxLength(Job.StreetAddressMaxLength);
        builder.Property(request => request.PostalCode).HasMaxLength(Job.PostalCodeMaxLength);
        builder.Property(request => request.City).HasMaxLength(Job.CityMaxLength);
        builder.HasOne(request => request.Supplier)
            .WithMany()
            .HasForeignKey(request => request.SupplierId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(request => request.Orderer)
            .WithMany()
            .HasForeignKey(request => request.OrdererId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.Property(request => request.SupplierId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(request => request.OrdererId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasIndex(request => new { request.TenantId, request.OrdererId, request.State });
        builder.HasIndex(request => request.CrossTenantId)
            .IsUnique()
            .HasFilter($"[{nameof(Order.CrossTenantId)}] IS NOT NULL");
        builder.HasIndex(request => request.LinkedCrossTenantId)
            .IsUnique()
            .HasFilter($"[{nameof(Order.LinkedCrossTenantId)}] IS NOT NULL");
        builder.ToTable(table => table.HasCheckConstraint("CK_Orders_PeriodInOrder", $"[{nameof(Order.PeriodEnd)}] >= [{nameof(Order.PeriodStart)}]"));
    }
}
