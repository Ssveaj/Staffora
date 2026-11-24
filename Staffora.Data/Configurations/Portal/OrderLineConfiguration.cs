using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Portal;

namespace Staffora.Data.Configurations.Portal;

public class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasKey(line => new { line.TenantId, line.OrderId, line.OfferedServiceId });
        builder.HasOne(line => line.Order)
            .WithMany(order => order.Lines)
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(line => line.OfferedService)
            .WithMany()
            .HasForeignKey(line => line.OfferedServiceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(line => line.OfferedServiceId);
        builder.ToTable(table => table.HasCheckConstraint("CK_OrderLines_Count", $"[{nameof(OrderLine.Count)}] BETWEEN {OrderLine.MinCount} AND {OrderLine.MaxCount}"));
    }
}
