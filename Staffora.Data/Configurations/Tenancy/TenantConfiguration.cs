using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data.Configurations.Tenancy;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public const string NameIndex = "IX_Tenants_Name";

    public const string NotOwnParentCheck = "CK_Tenants_ParentTenantId_NotSelf";

    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(500);
        builder.HasIndex(t => t.IsRootTenant).IsUnique().HasFilter("[IsRootTenant] = 1");
        builder.HasIndex(t => t.Name).IsUnique().HasDatabaseName(NameIndex);
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.ParentTenantId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.ToTable(table => table.HasCheckConstraint(NotOwnParentCheck, $"[{nameof(Tenant.ParentTenantId)}] IS NULL OR [{nameof(Tenant.ParentTenantId)}] <> [{nameof(Tenant.Id)}]"));
    }
}
