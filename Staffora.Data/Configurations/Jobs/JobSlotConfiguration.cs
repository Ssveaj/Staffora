using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Data.Configurations.Jobs;

public class JobSlotConfiguration : IEntityTypeConfiguration<JobSlot>
{
    public void Configure(EntityTypeBuilder<JobSlot> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(slot => slot.JobId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(slot => slot.Job)
            .WithMany(job => job.Slots)
            .HasForeignKey(slot => slot.JobId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.Property(slot => slot.Name).HasMaxLength(JobSlot.NameMaxLength).IsRequired();
        builder.Property(slot => slot.Description).HasMaxLength(JobSlot.DescriptionMaxLength);
        builder.HasIndex(slot => new { slot.JobId, slot.State });

        builder.Property(slot => slot.ServiceId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(slot => slot.Service)
            .WithMany()
            .HasForeignKey(slot => slot.ServiceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(slot => new { slot.TenantId, slot.ServiceId });

        builder.Ignore(slot => slot.Count);
    }
}
