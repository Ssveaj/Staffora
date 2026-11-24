using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Resources;

namespace Staffora.Data.Configurations.Resources;

public class ResourceQualificationConfiguration : IEntityTypeConfiguration<ResourceQualification>
{
    public void Configure(EntityTypeBuilder<ResourceQualification> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasKey(held => new { held.TenantId, held.ResourceId, held.QualificationId });
        builder.HasOne(held => held.Resource)
            .WithMany()
            .HasForeignKey(held => held.ResourceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(held => held.Qualification)
            .WithMany()
            .HasForeignKey(held => held.QualificationId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(held => held.QualificationId);
    }
}
