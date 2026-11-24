using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Catalogue;

namespace Staffora.Data.Configurations.Catalogue;

public class ServiceQualificationConfiguration : IEntityTypeConfiguration<ServiceQualification>
{
    public void Configure(EntityTypeBuilder<ServiceQualification> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasKey(named => new { named.TenantId, named.ServiceId, named.QualificationId });
        builder.HasOne(named => named.Service)
            .WithMany(service => service.Qualifications)
            .HasForeignKey(named => named.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(named => named.Qualification)
            .WithMany()
            .HasForeignKey(named => named.QualificationId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(named => named.QualificationId);
        builder.ToTable(table => table.HasCheckConstraint("CK_ServiceQualifications_Level", $"[{nameof(ServiceQualification.Level)}] IN (0, 1)"));
    }
}
