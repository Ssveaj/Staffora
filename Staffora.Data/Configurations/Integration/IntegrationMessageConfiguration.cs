using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data.Configurations.Integration;

public sealed class IntegrationMessageConfiguration : IEntityTypeConfiguration<IntegrationMessage>
{
    public void Configure(EntityTypeBuilder<IntegrationMessage> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(e => e.Sequence).UseIdentityColumn().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasIndex(e => e.Sequence).IsUnique();
        builder.Property(e => e.Kind).HasMaxLength(IntegrationMessage.MaxKindLength).IsRequired();
        builder.Property(e => e.Payload).HasMaxLength(IntegrationMessage.MaxPayloadLength).IsRequired();
        builder.Property(e => e.LastFailure).HasMaxLength(IntegrationMessage.MaxFailureLength);
        builder.Property(e => e.ClaimToken).IsConcurrencyToken();
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TargetTenantId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(e => new { e.State, e.NextAttempt });
        builder.HasIndex(e => new { e.TenantId, e.CorrelationId, e.Sequence });
    }
}
