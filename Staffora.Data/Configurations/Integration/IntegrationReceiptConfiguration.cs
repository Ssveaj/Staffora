using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Integration;

namespace Staffora.Data.Configurations.Integration;

public sealed class IntegrationReceiptConfiguration : IEntityTypeConfiguration<IntegrationReceipt>
{
    public const string OnePerMessageIndex = "IX_IntegrationReceipts_TenantId_MessageId";

    public void Configure(EntityTypeBuilder<IntegrationReceipt> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(e => e.Kind).HasMaxLength(IntegrationMessage.MaxKindLength).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.MessageId }).IsUnique().HasDatabaseName(OnePerMessageIndex);
    }
}
