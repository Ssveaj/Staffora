using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Data.Configurations.Scheduling;

public class ResourceAbsenceConfiguration : IEntityTypeConfiguration<ResourceAbsence>
{
    public void Configure(EntityTypeBuilder<ResourceAbsence> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(absence => absence.ResourceId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(absence => absence.Resource)
            .WithMany()
            .HasForeignKey(absence => absence.ResourceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(absence => new { absence.ResourceId, absence.State, absence.FirstDay });
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_ResourceAbsences_LastNotBeforeFirst", $"[{nameof(ResourceAbsence.LastDay)}] >= [{nameof(ResourceAbsence.FirstDay)}]");
            table.HasCheckConstraint("CK_ResourceAbsences_TimesInOrder", $"[{nameof(ResourceAbsence.FirstDay)}] <> [{nameof(ResourceAbsence.LastDay)}] OR [{nameof(ResourceAbsence.StartTime)}] IS NULL OR [{nameof(ResourceAbsence.EndTime)}] IS NULL OR [{nameof(ResourceAbsence.EndTime)}] > [{nameof(ResourceAbsence.StartTime)}]");
            table.HasCheckConstraint("CK_ResourceAbsences_EndNotMidnight", $"[{nameof(ResourceAbsence.EndTime)}] IS NULL OR [{nameof(ResourceAbsence.EndTime)}] > '00:00:00'");
            table.HasCheckConstraint("CK_ResourceAbsences_KnownKind", $"[{nameof(ResourceAbsence.Kind)}] IN ({string.Join(", ", Enum.GetValues<AbsenceKind>().Select(kind => (int)kind))})");
        });
    }
}
