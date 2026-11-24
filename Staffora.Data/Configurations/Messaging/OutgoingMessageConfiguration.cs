using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Messaging;

namespace Staffora.Data.Configurations.Messaging;

public sealed class OutgoingMessageConfiguration : IEntityTypeConfiguration<OutgoingMessage>
{
    public const int RecipientUserIdsMaxLength = 4000;

    public void Configure(EntityTypeBuilder<OutgoingMessage> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(e => e.MessageType).HasMaxLength(MessageType.MaxKeyLength).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Subject).HasMaxLength(MessageCatalogue.MaxSubjectLength).IsRequired();
        builder.Property(e => e.Text).HasMaxLength(MessageCatalogue.MaxTextLength).IsRequired();
        builder.Property(e => e.Link).HasMaxLength(MessageCatalogue.MaxLinkLength);
        builder.PrimitiveCollection(e => e.RecipientUserIds).HasMaxLength(RecipientUserIdsMaxLength).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.HistoryEntryId });
        builder.HasIndex(e => new { e.TenantId, e.Status });
        builder.HasIndex(e => new { e.Status, e.NextAttempt });
        builder.Property(e => e.Attempts).IsConcurrencyToken();
    }
}
