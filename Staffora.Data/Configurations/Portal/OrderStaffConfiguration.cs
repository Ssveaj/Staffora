using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Portal;

namespace Staffora.Data.Configurations.Portal;

public class OrderStaffConfiguration : IEntityTypeConfiguration<OrderStaff>
{
    public void Configure(EntityTypeBuilder<OrderStaff> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(staff => staff.Name).HasMaxLength(OrderStaff.NameMaxLength).IsRequired();
        builder.HasOne(staff => staff.Order)
            .WithMany()
            .HasForeignKey(staff => staff.OrderId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(staff => new { staff.OrderId, staff.Position });
    }
}
