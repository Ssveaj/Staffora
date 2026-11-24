using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core.Entities;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Test.Unit.Data;

public class TenantFilterCoverageTests
{
    private static readonly Dictionary<Type, string> exceptions = new()
    {
        [typeof(OrganizationUnitAttributeDefinition)] = "Attribute definitions: a null TenantId is global; their own filter shows the global ones and the runtime tenant's",
        [typeof(ApplicationUserAttributeDefinition)] = "Attribute definitions: a null TenantId is global; their own filter shows the global ones and the runtime tenant's",
        [typeof(Staffora.Core.Entities.EntityAttributes.JobAttributes.JobAttributeDefinition)] = "Attribute definitions: a null TenantId is global; their own filter shows the global ones and the runtime tenant's",
        [typeof(ErrorLog)] = "The error log, a platform table: its TenantId labels the row; its own filter shows nothing outside system scope",
    };

    [Fact]
    public void The_tenant_table_attribute_definitions_attribute_values_and_the_platform_tables_are_filtered()
    {
        string[] filtered =
        [
            nameof(Tenant), nameof(OrganizationUnitAttributeDefinition), nameof(ApplicationUserAttributeDefinition),
            nameof(OrganizationUnitAttribute), nameof(ApplicationUserAttribute), nameof(ImpersonationAudit), nameof(ErrorLog),
            "JobAttributeDefinition", "JobAttribute",
        ];

        var unfiltered = ModelUnderTest.Model.GetEntityTypes()
            .Where(entityType => filtered.Contains(entityType.ClrType.Name) && !entityType.GetDeclaredQueryFilters().Any())
            .Select(entityType => entityType.DisplayName());

        Assert.Equal(filtered.Length, ModelUnderTest.Model.GetEntityTypes().Count(entityType => filtered.Contains(entityType.ClrType.Name)));
        Assert.Empty(unfiltered);
    }

    [Fact]
    public void Every_entity_with_a_TenantId_is_filtered_or_listed_as_an_exception()
    {
        var withTenantId = ModelUnderTest.Model.GetEntityTypes()
            .Where(entityType => entityType.FindProperty(nameof(ITenantEntity.TenantId)) is not null)
            .ToList();

        var unfiltered = withTenantId
            .Where(entityType => !exceptions.ContainsKey(entityType.ClrType))
            .Where(entityType => !typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType) || !FiltersOnTenantId(entityType))
            .Select(entityType => entityType.DisplayName());
        var stale = exceptions.Keys
            .Where(type => !withTenantId.Any(entityType => entityType.ClrType == type) || typeof(ITenantEntity).IsAssignableFrom(type))
            .Select(type => $"{type.Name} is listed as an exception but has no TenantId or is a tenant row");

        Assert.Contains(withTenantId, entityType => entityType.ClrType == typeof(TenantRole));
        Assert.Empty(unfiltered.Concat(stale));
    }

    [Fact]
    public void Every_tenant_rows_TenantId_is_read_only_after_save()
    {
        var writable = ModelUnderTest.TenantRows
            .Where(entityType => entityType.FindProperty(nameof(ITenantEntity.TenantId))!.GetAfterSaveBehavior() != PropertySaveBehavior.Throw)
            .Select(entityType => entityType.DisplayName());

        Assert.Empty(writable);
    }

    [Fact]
    public void The_filter_check_finds_a_tenant_row_without_a_filter()
    {
        var builder = new ModelBuilder();
        builder.Entity<UnfilteredRow>().HasKey(row => row.Id);
        var model = builder.FinalizeModel();

        Assert.False(FiltersOnTenantId(model.FindEntityType(typeof(UnfilteredRow))!));
    }

    private static bool FiltersOnTenantId(IReadOnlyEntityType entityType) =>
        entityType.GetDeclaredQueryFilters().Any(filter => filter.Expression?.ToString().Contains($".{nameof(ITenantEntity.TenantId)}", StringComparison.Ordinal) == true);

    private sealed class UnfilteredRow : TenantEntityBase;
}
