using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Data.Configurations.Scheduling;

public class ClosedDayConfiguration : IEntityTypeConfiguration<ClosedDay>
{
    public const string RangeCheck = "CK_ClosedDays_LastNotBeforeFirst";

    public void Configure(EntityTypeBuilder<ClosedDay> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(closedDay => closedDay.Name).HasMaxLength(ClosedDay.NameMaxLength).IsRequired();
        builder.HasOne(closedDay => closedDay.OrganizationUnit)
            .WithMany()
            .HasForeignKey(closedDay => closedDay.OrganizationUnitId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(closedDay => new { closedDay.TenantId, closedDay.FirstDay, closedDay.LastDay });
        builder.ToTable(table => table.HasCheckConstraint(RangeCheck, $"[{nameof(ClosedDay.LastDay)}] >= [{nameof(ClosedDay.FirstDay)}]"));
    }
}
