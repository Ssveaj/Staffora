using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Organization;

namespace Staffora.Data.Configurations.Organization;

public class OrganizationUnitUserRoleConfiguration : IEntityTypeConfiguration<OrganizationUnitUserRole>
{
    public void Configure(EntityTypeBuilder<OrganizationUnitUserRole> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(r => r.TenantRole)
            .WithMany()
            .HasForeignKey(r => r.TenantRoleId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(r => r.OrganizationUnit)
            .WithMany()
            .HasForeignKey(r => r.OrganizationUnitId)
            .IsRequired()
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(r => new { r.TenantId, r.UserId, r.TenantRoleId, r.OrganizationUnitId }).IsUnique();
    }
}
