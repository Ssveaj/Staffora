using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data.Configurations.Organization;

public class OrganizationUnitConfiguration : IEntityTypeConfiguration<OrganizationUnit>
{
    public const string OneCompanyRootIndex = "IX_OrganizationUnits_TenantId_OneCompanyRoot";

    public const string RootHasNoParentCheck = "CK_OrganizationUnits_ParentUnlessCompany";

    public const string NotOwnParentCheck = "CK_OrganizationUnits_NotOwnParent";

    public const string SiblingNameIndex = "IX_OrganizationUnits_TenantId_ParentId_Name";

    public void Configure(EntityTypeBuilder<OrganizationUnit> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(u => u.Name).HasMaxLength(OrganizationUnit.NameMaxLength).IsRequired();
        builder.Property(u => u.Description).HasMaxLength(OrganizationUnit.DescriptionMaxLength);

        builder.HasOne<Tenant>()
            .WithMany(t => t.OrganizationUnits)
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(u => u.Parent)
            .WithMany(u => u.Children)
            .HasForeignKey(u => u.ParentId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(u => new { u.TenantId, u.ParentId });
        builder.HasIndex(u => new { u.TenantId, u.ParentId, u.Name }).IsUnique();

        var company = (int)OrganizationUnitType.Company;
        builder.HasIndex(u => u.TenantId, OneCompanyRootIndex).IsUnique().HasFilter($"[UnitType] = {company}");
        builder.ToTable(table =>
        {
            table.HasCheckConstraint(RootHasNoParentCheck, $"([UnitType] = {company} AND [ParentId] IS NULL) OR ([UnitType] <> {company} AND [ParentId] IS NOT NULL)");
            table.HasCheckConstraint(NotOwnParentCheck, "[ParentId] IS NULL OR [ParentId] <> [Id]");
        });
    }
}
