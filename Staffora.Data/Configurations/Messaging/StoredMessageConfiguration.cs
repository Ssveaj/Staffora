using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Messaging;

namespace Staffora.Data.Configurations.Messaging;

public sealed class StoredMessageConfiguration : IEntityTypeConfiguration<StoredMessage>
{
    public void Configure(EntityTypeBuilder<StoredMessage> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(e => e.MessageType).HasMaxLength(MessageType.MaxKeyLength).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Subject).HasMaxLength(MessageCatalogue.MaxSubjectLength).IsRequired();
        builder.Property(e => e.Text).HasMaxLength(MessageCatalogue.MaxTextLength).IsRequired();
        builder.Property(e => e.Link).HasMaxLength(MessageCatalogue.MaxLinkLength);
        builder.HasIndex(e => e.OutgoingMessageId).IsUnique();
        builder.HasOne(e => e.OutgoingMessage)
            .WithMany()
            .HasForeignKey(e => e.OutgoingMessageId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(e => e.Recipients)
            .WithOne(e => e.StoredMessage)
            .HasForeignKey(e => e.StoredMessageId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
