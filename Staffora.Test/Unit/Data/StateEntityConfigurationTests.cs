using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;

namespace Staffora.Test.Unit.Data;

public class StateEntityConfigurationTests
{
    private static readonly string[] stateTokens = [nameof(IStateEntity<>.State), nameof(IStateEntity.IsInTransition)];

    [Fact]
    public void Every_state_entity_of_the_application_has_State_and_IsInTransition_as_its_only_concurrency_tokens()
    {
        var stateEntities = StateEntities(ModelUnderTest.Model);

        Assert.Contains(stateEntities, entityType => entityType.ClrType == typeof(Tenant));
        Assert.Empty(TokenViolations(stateEntities));
    }

    [Fact]
    public void The_tokens_come_from_the_model_not_from_attributes_on_the_entities()
    {
        var attributed = new[] { typeof(StateEntityBase<>), typeof(TenantStateEntityBase<>) }
            .SelectMany(type => type.GetProperties())
            .Where(property => property.GetCustomAttributes(inherit: true).Any(attribute => attribute.GetType().Name is "ConcurrencyCheckAttribute" or "TimestampAttribute"))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}");

        Assert.Empty(attributed);
    }

    [Fact]
    public void Tenant_has_no_active_flag_beside_its_state()
    {
        Assert.Null(typeof(Tenant).GetProperty("IsActive"));
        Assert.Null(ModelUnderTest.Model.FindEntityType(typeof(Tenant))!.FindProperty("IsActive"));
    }

    [Fact]
    public void A_user_has_no_active_flag_beside_its_state()
    {
        Assert.Null(typeof(ApplicationUser).GetProperty("IsActive"));
        Assert.Null(ModelUnderTest.Model.FindEntityType(typeof(ApplicationUser))!.FindProperty("IsActive"));
        Assert.Contains(StateEntities(ModelUnderTest.Model), entityType => entityType.ClrType == typeof(ApplicationUser));
    }

    internal static List<IEntityType> StateEntities(IModel model) =>
        model.GetEntityTypes().Where(entityType => typeof(IStateEntity).IsAssignableFrom(entityType.ClrType)).ToList();

    private static IEnumerable<string> ExpectedTokens(Type entity) =>
        entity == typeof(ApplicationUser) ? [.. stateTokens, nameof(ApplicationUser.ConcurrencyStamp)]
        : typeof(Staffora.Core.Entities.EntityAttributes.IEntityAttributeDefinition).IsAssignableFrom(entity) ? [.. stateTokens, nameof(Staffora.Core.Entities.EntityAttributes.EntityAttributeDefinitionBase<object>.RowVersion)]
        : stateTokens;

    internal static IEnumerable<string> TokenViolations(IEnumerable<IEntityType> stateEntities) =>
        stateEntities
            .Select(entityType => (EntityType: entityType, Tokens: entityType.GetProperties().Where(property => property.IsConcurrencyToken).Select(property => property.Name).Order().ToList()))
            .Where(entry => !entry.Tokens.SequenceEqual(ExpectedTokens(entry.EntityType.ClrType).Order()))
            .Select(entry => $"{entry.EntityType.DisplayName()}: [{string.Join(", ", entry.Tokens)}]");
}
