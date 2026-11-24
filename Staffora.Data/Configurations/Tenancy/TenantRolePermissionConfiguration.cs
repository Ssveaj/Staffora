using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data.Configurations.Tenancy;

public class TenantRolePermissionConfiguration : IEntityTypeConfiguration<TenantRolePermission>
{
    public const string OnePerPermissionIndex = "IX_TenantRolePermissions_TenantId_TenantRoleId_Permission";

    public void Configure(EntityTypeBuilder<TenantRolePermission> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasOne(p => p.TenantRole)
            .WithMany(r => r.Permissions)
            .HasForeignKey(p => p.TenantRoleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => new { p.TenantId, p.TenantRoleId, p.Permission })
            .IsUnique()
            .HasDatabaseName(OnePerPermissionIndex);
    }
}
