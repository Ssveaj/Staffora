using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;

namespace Staffora.Test.Unit.Data;

public class TenantPurgePlanTests
{
    private static IReadOnlyList<TenantPurgeStep> Plan => TenantPurge.PlanFor(ModelUnderTest.Model);

    [Fact]
    public void Every_entity_type_is_purged_with_its_tenant_or_listed_outside_the_tenant_with_a_reason()
    {
        var planned = Plan.SelectMany(step => step.EntityType.GetDerivedTypesInclusive()).ToHashSet();

        var unclassified = ModelUnderTest.Model.GetEntityTypes()
            .Where(type => !type.IsOwned() && !planned.Contains(type) && !TenantPurge.OutsideTheTenant.ContainsKey(type.GetRootType().ClrType))
            .Select(type => type.DisplayName());
        var stale = TenantPurge.OutsideTheTenant.Keys
            .Where(type => ModelUnderTest.Model.FindEntityType(type) is null || planned.Any(entity => entity.ClrType == type))
            .Select(type => type.Name);

        Assert.Empty(unclassified.Concat(stale));
        Assert.All(TenantPurge.OutsideTheTenant.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void No_tenant_row_custom_field_or_value_is_left_out_of_the_purge()
    {
        var planned = Plan.SelectMany(step => step.EntityType.GetDerivedTypesInclusive()).Select(type => type.ClrType).ToHashSet();

        var missing = ModelUnderTest.Model.GetEntityTypes()
            .Where(type => typeof(ITenantEntity).IsAssignableFrom(type.ClrType) || typeof(IEntityAttributeDefinition).IsAssignableFrom(type.ClrType) || typeof(IEntityAttributeValue).IsAssignableFrom(type.ClrType))
            .Where(type => !planned.Contains(type.ClrType))
            .Select(type => type.DisplayName());

        Assert.Empty(missing);
        Assert.Contains(typeof(ApplicationUser), planned);
        Assert.Contains(typeof(Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>), planned);
        Assert.DoesNotContain(typeof(Tenant), planned);
        Assert.DoesNotContain(typeof(ErrorLog), planned);
        Assert.DoesNotContain(typeof(ImpersonationAudit), planned);
    }

    [Fact]
    public void Every_row_is_removed_before_the_rows_it_refers_to_so_no_NoAction_key_is_broken()
    {
        var position = Plan.Select((step, index) => (step.EntityType, index)).ToDictionary(pair => pair.EntityType, pair => pair.index);

        var broken = Plan
            .SelectMany(step => step.EntityType.GetDerivedTypesInclusive().SelectMany(type => type.GetDeclaredForeignKeys()).Select(key => (Step: step, Key: key)))
            .Where(pair => position.TryGetValue(pair.Key.PrincipalEntityType.GetRootType(), out var principal) && pair.Key.PrincipalEntityType.GetRootType() != pair.Step.EntityType && principal <= position[pair.Step.EntityType])
            .Select(pair => $"{pair.Step.EntityType.DisplayName()} -> {pair.Key.PrincipalEntityType.DisplayName()}");

        Assert.Empty(broken);
        Assert.Equal(nameof(ApplicationUser), Plan[^1].EntityType.ClrType.Name);
    }

    [Fact]
    public void A_row_owns_its_tenant_by_its_TenantId_or_through_a_tenant_row_and_self_references_are_followed()
    {
        Assert.All(Plan, step => Assert.True(step.TenantProperty is not null ^ step.Owner is not null, step.EntityType.DisplayName()));
        Assert.All(Plan.Where(step => step.Owner is not null), step => Assert.False(step.Owner!.PrincipalEntityType.GetRootType().FindProperty(nameof(ITenantEntity.TenantId))!.IsNullable, step.EntityType.DisplayName()));

        var units = Plan.Single(step => step.ClrType == typeof(Staffora.Core.Entities.Organization.OrganizationUnit));
        Assert.Contains(units.SelfReferences, key => key.Properties[0].Name == nameof(Staffora.Core.Entities.Organization.OrganizationUnit.ParentId));
        var users = Plan.Single(step => step.ClrType == typeof(ApplicationUser));
        Assert.Contains(users.SelfReferences, key => key.Properties[0].Name == nameof(ITenantEntity.CreatedById));
    }

    [Fact]
    public void A_user_the_impersonation_audit_names_is_kept_and_only_users_are_ever_kept()
    {
        var users = Plan.Single(step => step.ClrType == typeof(ApplicationUser));

        Assert.Equal(
            [nameof(ImpersonationAudit.ImpersonatorId), nameof(ImpersonationAudit.TargetUserId)],
            users.OutsideReferences.Where(key => key.DeclaringEntityType.ClrType == typeof(ImpersonationAudit)).Select(key => key.Properties[0].Name).Order(StringComparer.Ordinal));
        Assert.Equal([typeof(ApplicationUser)], Plan.Where(step => step.OutsideReferences.Count > 0).Select(step => step.ClrType));
    }

    [Fact]
    public void The_runtime_model_gives_the_same_plan_as_the_design_time_model()
    {
        using var context = new ApplicationDbContext(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));

        Assert.Equal(Plan.Select(step => step.EntityType.Name), TenantPurge.PlanFor(context.Model).Select(step => step.EntityType.Name));
    }

    [Fact]
    public void An_entity_type_that_belongs_to_no_tenant_and_is_not_listed_stops_the_plan()
    {
        var builder = new ModelBuilder();
        builder.Entity<Stray>().HasKey(stray => stray.Id);

        var refused = Assert.Throws<InvalidOperationException>(() => TenantPurge.PlanFor(builder.FinalizeModel()));

        Assert.Contains(nameof(Stray), refused.Message, StringComparison.Ordinal);
    }

    private sealed class Stray
    {
        public Guid Id { get; set; }
    }
}
