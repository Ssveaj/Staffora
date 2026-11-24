using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Data.Configurations.Scheduling;

public class WorkPatternSegmentConfiguration : IEntityTypeConfiguration<WorkPatternSegment>
{
    public void Configure(EntityTypeBuilder<WorkPatternSegment> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasOne(segment => segment.WorkPattern)
            .WithMany(pattern => pattern.Segments)
            .HasForeignKey(segment => segment.WorkPatternId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(segment => new { segment.WorkPatternId, segment.DayIndex });
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_WorkPatternSegments_DayIndex", $"[{nameof(WorkPatternSegment.DayIndex)}] >= 0");
            table.HasCheckConstraint("CK_WorkPatternSegments_StartBeforeEnd", $"[{nameof(WorkPatternSegment.Start)}] < [{nameof(WorkPatternSegment.End)}]");
        });
    }
}
