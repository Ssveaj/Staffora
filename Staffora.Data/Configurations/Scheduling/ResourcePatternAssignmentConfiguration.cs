using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Data.Configurations.Scheduling;

public class ResourcePatternAssignmentConfiguration : IEntityTypeConfiguration<ResourcePatternAssignment>
{
    public void Configure(EntityTypeBuilder<ResourcePatternAssignment> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(assignment => assignment.ResourceId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(assignment => assignment.Resource)
            .WithMany()
            .HasForeignKey(assignment => assignment.ResourceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(assignment => assignment.WorkPattern)
            .WithMany()
            .HasForeignKey(assignment => assignment.WorkPatternId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(assignment => new { assignment.ResourceId, assignment.State, assignment.EffectiveFrom });
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_ResourcePatternAssignments_EndNotBeforeStart", $"[{nameof(ResourcePatternAssignment.EffectiveTo)}] IS NULL OR [{nameof(ResourcePatternAssignment.EffectiveTo)}] >= [{nameof(ResourcePatternAssignment.EffectiveFrom)}]");
            table.HasCheckConstraint("CK_ResourcePatternAssignments_AnchorMonday", $"DATEDIFF(day, '19000101', [{nameof(ResourcePatternAssignment.CycleAnchor)}]) % 7 = 0");
        });
    }
}
