using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data.Configurations.Tenancy;

public class TenantRoleConfiguration : IEntityTypeConfiguration<TenantRole>
{
    public void Configure(EntityTypeBuilder<TenantRole> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(500);

        builder.HasIndex(r => r.TenantId, "IX_TenantRoles_TenantId_SystemOwner")
            .IsUnique()
            .HasFilter($"[{nameof(TenantRole.IsSystemOwner)}] = 1");
        builder.HasOne(r => r.Tenant)
            .WithMany(t => t.TenantRoles)
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
