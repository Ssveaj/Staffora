using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Messaging;

namespace Staffora.Data.Configurations.Messaging;

public sealed class MessageRecipientConfiguration : IEntityTypeConfiguration<MessageRecipient>
{
    public void Configure(EntityTypeBuilder<MessageRecipient> builder)
    {
        builder.ConfigureTenantEntity();
        builder.HasIndex(e => new { e.StoredMessageId, e.UserId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.UserId, e.IsRead });
        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
