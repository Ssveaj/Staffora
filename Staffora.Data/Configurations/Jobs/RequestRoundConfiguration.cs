using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Data.Configurations.Jobs;

public class RequestRoundConfiguration : IEntityTypeConfiguration<RequestRound>
{
    public void Configure(EntityTypeBuilder<RequestRound> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(round => round.JobSlotId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(round => round.Slot)
            .WithMany(slot => slot.Rounds)
            .HasForeignKey(round => round.JobSlotId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(round => new { round.JobSlotId, round.State });
        builder.HasIndex(round => new { round.TenantId, round.State, round.RespondBy })
            .HasFilter($"[{nameof(RequestRound.RespondBy)}] IS NOT NULL")
            .HasDatabaseName("IX_RequestRounds_RespondBy");
    }
}
