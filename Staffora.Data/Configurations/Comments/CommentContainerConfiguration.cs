using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Comments;

namespace Staffora.Data.Configurations.Comments;

public sealed class CommentContainerConfiguration : IEntityTypeConfiguration<CommentContainer>
{
    public void Configure(EntityTypeBuilder<CommentContainer> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId, e.Audience }).IsUnique();
        builder.HasMany(e => e.Comments)
            .WithOne(e => e.CommentContainer)
            .HasForeignKey(e => e.CommentContainerId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
