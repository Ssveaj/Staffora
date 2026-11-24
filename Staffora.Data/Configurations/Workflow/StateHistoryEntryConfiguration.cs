using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Workflow;

namespace Staffora.Data.Configurations.Workflow;

public sealed class StateHistoryEntryConfiguration : IEntityTypeConfiguration<StateHistoryEntry>
{
    public void Configure(EntityTypeBuilder<StateHistoryEntry> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(e => e.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.TransitionKey).HasMaxLength(100).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId, e.Created });
        builder.HasIndex(e => new { e.TenantId, e.CorrelationId });
    }
}
