using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Data.Configurations.Jobs;

public class JobSlotQualificationConfiguration : IEntityTypeConfiguration<JobSlotQualification>
{
    public void Configure(EntityTypeBuilder<JobSlotQualification> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasKey(required => new { required.TenantId, required.JobSlotId, required.QualificationId });
        builder.HasOne(required => required.JobSlot)
            .WithMany(slot => slot.Qualifications)
            .HasForeignKey(required => required.JobSlotId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(required => required.Qualification)
            .WithMany()
            .HasForeignKey(required => required.QualificationId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(required => required.QualificationId);

        builder.ToTable(table => table.HasCheckConstraint("CK_JobSlotQualifications_Level", $"[{nameof(JobSlotQualification.Level)}] IN (0, 1)"));
    }
}
