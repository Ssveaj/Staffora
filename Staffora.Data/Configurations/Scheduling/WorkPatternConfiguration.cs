using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;

namespace Staffora.Data.Configurations.Scheduling;

public class WorkPatternConfiguration : IEntityTypeConfiguration<WorkPattern>
{
    public void Configure(EntityTypeBuilder<WorkPattern> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(pattern => pattern.Name).HasMaxLength(WorkPattern.NameMaxLength).IsRequired();
        builder.Property(pattern => pattern.Description).HasMaxLength(WorkPattern.DescriptionMaxLength);
        builder.ToTable(table => table.HasCheckConstraint("CK_WorkPatterns_CycleWeeks", $"[{nameof(WorkPattern.CycleWeeks)}] BETWEEN 1 AND {WorkPatternRules.MaxCycleWeeks}"));
    }
}
