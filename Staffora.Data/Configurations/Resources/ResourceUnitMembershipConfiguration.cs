using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Resources;

namespace Staffora.Data.Configurations.Resources;

public class ResourceUnitMembershipConfiguration : IEntityTypeConfiguration<ResourceUnitMembership>
{
    public void Configure(EntityTypeBuilder<ResourceUnitMembership> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasKey(membership => new { membership.TenantId, membership.ResourceId, membership.OrganizationUnitId });
        builder.HasOne(membership => membership.Resource)
            .WithMany(resource => resource.Memberships)
            .HasForeignKey(membership => membership.ResourceId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(membership => membership.OrganizationUnit)
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationUnitId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(membership => membership.OrganizationUnitId);
    }
}
