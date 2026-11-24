using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Resources;

namespace Staffora.Data.Configurations.Resources;

public class QualificationConfiguration : IEntityTypeConfiguration<Qualification>
{
    public const string UniqueNameIndex = "IX_Qualifications_TenantId_Name";

    public void Configure(EntityTypeBuilder<Qualification> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(qualification => qualification.Name).HasMaxLength(Qualification.NameMaxLength).IsRequired();
        builder.Property(qualification => qualification.Description).HasMaxLength(Qualification.DescriptionMaxLength);
        builder.HasIndex(qualification => new { qualification.TenantId, qualification.Name })
            .IsUnique()
            .HasDatabaseName(UniqueNameIndex);
    }
}
