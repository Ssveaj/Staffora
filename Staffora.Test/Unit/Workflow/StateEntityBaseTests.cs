using Staffora.Core.Entities;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;

namespace Staffora.Test.Unit.Workflow;

public class StateEntityBaseTests
{
    private enum SampleState
    {
        Draft = 0,
        Active = 1,
    }

    private sealed class SampleTenantFreeEntity : StateEntityBase<SampleState>;

    private sealed class SampleTenantOwnedEntity : TenantStateEntityBase<SampleState>;

    [Fact]
    public void Tenant_has_exactly_one_Guid_id()
    {
        var ids = typeof(Tenant).GetProperties().Where(property => property.Name == nameof(Tenant.Id)).ToList();

        var id = Assert.Single(ids);
        Assert.Equal(typeof(Guid), id.PropertyType);
    }

    [Fact]
    public void Tenant_is_a_state_entity_that_belongs_to_no_tenant()
    {
        Assert.True(typeof(IStateEntity<TenantState>).IsAssignableFrom(typeof(Tenant)));
        Assert.False(typeof(ITenantEntity).IsAssignableFrom(typeof(Tenant)));
    }

    [Fact]
    public void Tenant_declares_no_member_that_hides_an_inherited_one()
    {
        var inherited = typeof(StateEntityBase<TenantState>).GetProperties().Select(property => property.Name).ToHashSet();

        var hiding = typeof(Tenant).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(property => inherited.Contains(property.Name))
            .Select(property => property.Name);

        Assert.Empty(hiding);
    }

    [Fact]
    public void Both_base_types_implement_the_state_entity_contract()
    {
        IStateEntity<SampleState> tenantFree = new SampleTenantFreeEntity { State = SampleState.Active };
        IStateEntity<SampleState> tenantOwned = new SampleTenantOwnedEntity { State = SampleState.Active };

        Assert.Equal(SampleState.Active, tenantFree.State);
        Assert.Equal(SampleState.Active, tenantOwned.State);
        Assert.IsAssignableFrom<ITenantEntity>(tenantOwned);
        Assert.False(tenantFree is ITenantEntity);
    }

    [Fact]
    public void State_entity_keys_are_Guids()
    {
        Assert.Equal(typeof(Guid), typeof(StateEntityBase<SampleState>).GetProperty(nameof(IStateEntity.Id))!.PropertyType);
        Assert.Equal(typeof(Guid), typeof(TenantStateEntityBase<SampleState>).GetProperty(nameof(IStateEntity.Id))!.PropertyType);
    }
}
