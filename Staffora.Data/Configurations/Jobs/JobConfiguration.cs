using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Data.Configurations.Jobs;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(job => job.Name).HasMaxLength(Job.NameMaxLength).IsRequired();
        builder.Property(job => job.Description).HasMaxLength(Job.DescriptionMaxLength);
        builder.Property(job => job.PlaceName).HasMaxLength(Job.PlaceNameMaxLength);
        builder.Property(job => job.StreetAddress).HasMaxLength(Job.StreetAddressMaxLength);
        builder.Property(job => job.PostalCode).HasMaxLength(Job.PostalCodeMaxLength);
        builder.Property(job => job.City).HasMaxLength(Job.CityMaxLength);
        builder.Property(job => job.AccessNotes).HasMaxLength(Job.AccessNotesMaxLength);
        builder.HasOne(job => job.Customer)
            .WithMany()
            .HasForeignKey(job => job.CustomerId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(job => job.Contact)
            .WithMany()
            .HasForeignKey(job => job.ContactId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(job => job.OrganizationUnit)
            .WithMany()
            .HasForeignKey(job => job.OrganizationUnitId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(job => job.Coordinator)
            .WithMany()
            .HasForeignKey(job => job.CoordinatorUserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(job => new { job.TenantId, job.State, job.PeriodStart });
        builder.HasIndex(job => job.CrossTenantId)
            .IsUnique()
            .HasFilter($"[{nameof(Job.CrossTenantId)}] IS NOT NULL");
        builder.HasIndex(job => job.LinkedCrossTenantId)
            .IsUnique()
            .HasFilter($"[{nameof(Job.LinkedCrossTenantId)}] IS NOT NULL");
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_Jobs_PeriodInOrder", $"[{nameof(Job.PeriodEnd)}] >= [{nameof(Job.PeriodStart)}]");

            table.HasCheckConstraint(
                "CK_Jobs_ScheduleTimes",
                $"([{nameof(Job.Schedule)}] = {(int)JobSchedule.Single} AND [{nameof(Job.StartTime)}] IS NOT NULL AND [{nameof(Job.EndTime)}] IS NOT NULL AND [{nameof(Job.StartTime)}] <> [{nameof(Job.EndTime)}] AND DATEDIFF(day, [{nameof(Job.PeriodStart)}], [{nameof(Job.PeriodEnd)}]) <= 1)"
                + $" OR ([{nameof(Job.Schedule)}] = {(int)JobSchedule.Recurring} AND [{nameof(Job.StartTime)}] IS NULL AND [{nameof(Job.EndTime)}] IS NULL)");
        });
    }
}
