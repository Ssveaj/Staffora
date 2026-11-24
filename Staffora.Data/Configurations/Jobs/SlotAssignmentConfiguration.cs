using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Data.Configurations.Jobs;

public class SlotAssignmentConfiguration : IEntityTypeConfiguration<SlotAssignment>
{
    public void Configure(EntityTypeBuilder<SlotAssignment> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(assignment => assignment.JobSlotId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Save);
        builder.Property(assignment => assignment.ResourceId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(assignment => assignment.Slot)
            .WithMany(slot => slot.Assignments)
            .HasForeignKey(assignment => assignment.JobSlotId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(assignment => assignment.Resource)
            .WithMany()
            .HasForeignKey(assignment => assignment.ResourceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(assignment => assignment.JobSlotId)
            .IsUnique()
            .HasFilter($"[{nameof(SlotAssignment.State)}] = {(int)SlotAssignmentState.Assigned}")
            .HasDatabaseName("IX_SlotAssignments_OneHolder");
        builder.HasIndex(assignment => new { assignment.JobSlotId, assignment.ResourceId })
            .IsUnique()
            .HasFilter($"[{nameof(SlotAssignment.State)}] IN ({(int)SlotAssignmentState.Requested}, {(int)SlotAssignmentState.Accepted}, {(int)SlotAssignmentState.Assigned})")
            .HasDatabaseName("IX_SlotAssignments_OneLivePerStaff");
        builder.HasIndex(assignment => new { assignment.ResourceId, assignment.State });
        builder.HasIndex(assignment => new { assignment.TenantId, assignment.State, assignment.RespondBy })
            .HasFilter($"[{nameof(SlotAssignment.RespondBy)}] IS NOT NULL")
            .HasDatabaseName("IX_SlotAssignments_RespondBy");

        builder.Property(assignment => assignment.RequestRoundId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(assignment => assignment.Round)
            .WithMany(round => round.Requests)
            .HasForeignKey(assignment => assignment.RequestRoundId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
