using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data.Configurations.Platform;

public sealed class ImpersonationAuditConfiguration : IEntityTypeConfiguration<ImpersonationAudit>
{
    public void Configure(EntityTypeBuilder<ImpersonationAudit> builder)
    {
        builder.HasKey(audit => audit.Id);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(audit => audit.ImpersonatorId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(audit => audit.TargetUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(audit => audit.TargetTenantId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(audit => new { audit.ImpersonatorId, audit.Ended });
        builder.HasIndex(audit => audit.Started);
    }
}
