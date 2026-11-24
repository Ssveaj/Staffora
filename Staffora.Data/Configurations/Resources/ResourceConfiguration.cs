using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Resources;

namespace Staffora.Data.Configurations.Resources;

public class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(resource => resource.UserId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasIndex(resource => resource.UserId).IsUnique();
        builder.HasOne(resource => resource.User)
            .WithMany()
            .HasForeignKey(resource => resource.UserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
