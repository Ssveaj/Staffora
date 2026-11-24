using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Settings;

namespace Staffora.Data.Configurations.Tenancy;

public class TenantSettingConfiguration : IEntityTypeConfiguration<TenantSetting>
{
    public void Configure(EntityTypeBuilder<TenantSetting> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(setting => setting.Key).HasMaxLength(TenantSettingDefinition.KeyMaxLength).IsRequired();
        builder.Property(setting => setting.Value).HasMaxLength(TenantSettingDefinition.ValueMaxLength).IsRequired();
        builder.Property(setting => setting.RowVersion).IsRowVersion();
        builder.HasIndex(setting => new { setting.TenantId, setting.Key }).IsUnique();
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(setting => setting.TenantId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
