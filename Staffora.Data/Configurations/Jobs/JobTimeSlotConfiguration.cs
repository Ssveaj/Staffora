using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Data.Configurations.Jobs;

public class JobTimeSlotConfiguration : IEntityTypeConfiguration<JobTimeSlot>
{
    public void Configure(EntityTypeBuilder<JobTimeSlot> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasOne(slot => slot.Job)
            .WithMany(job => job.TimeSlots)
            .HasForeignKey(slot => slot.JobId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(slot => new { slot.JobId, slot.Weekday });
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_JobTimeSlots_Weekday", $"[{nameof(JobTimeSlot.Weekday)}] BETWEEN 1 AND 7");
            table.HasCheckConstraint("CK_JobTimeSlots_StartBeforeEnd", $"[{nameof(JobTimeSlot.Start)}] < [{nameof(JobTimeSlot.End)}]");
        });
    }
}
