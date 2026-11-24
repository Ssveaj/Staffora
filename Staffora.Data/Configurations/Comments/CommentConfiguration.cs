using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Portal;

namespace Staffora.Data.Configurations.Comments;

public sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(e => e.Text).HasMaxLength(Comment.MaxTextLength).IsRequired();
        builder.Property(e => e.TransitionKey).HasMaxLength(100);
        builder.HasIndex(e => new { e.TenantId, e.CommentContainerId, e.Created });
        builder.HasIndex(e => e.CrossTenantId)
            .IsUnique()
            .HasFilter($"[{nameof(Comment.CrossTenantId)}] IS NOT NULL");
        builder.HasIndex(e => new { e.TenantId, e.LinkedCrossTenantId })
            .IsUnique()
            .HasFilter($"[{nameof(Comment.LinkedCrossTenantId)}] IS NOT NULL");
        builder.HasOne<CustomerContact>()
            .WithMany()
            .HasForeignKey(e => e.AuthorContactId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(e => e.AuthorSupplierId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
