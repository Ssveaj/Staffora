using Staffora.Core.Entities.Tenancy;

namespace Staffora.Test.Unit.Authorization;

public class TenantEditionTests
{
    [Fact]
    public void A_managed_tenant_is_a_customer_portal_and_every_other_tenant_a_staffing_company()
    {
        Assert.Equal(TenantEdition.CustomerPortal, TenantEditions.Of(Guid.NewGuid()));
        Assert.Equal(TenantEdition.Staffing, TenantEditions.Of(null));
    }

    [Fact]
    public void The_two_editions_split_the_grantable_permissions_the_portals_area_to_the_portal()
    {
        var portal = TenantEditions.Ceiling(TenantEdition.CustomerPortal);
        var staffing = TenantEditions.Ceiling(TenantEdition.Staffing);

        Assert.Equal(RolePermissions.Portal, portal);
        Assert.Empty(portal.Intersect(staffing));
        Assert.Equal(RolePermissions.Grantable.Order(), portal.Concat(staffing).Order());
        Assert.Contains(RolePermission.CustomerPortalEnable, staffing);
        Assert.DoesNotContain(RolePermission.UsersCreate, portal);
        Assert.False(TenantEditions.Allows((TenantEdition)99, RolePermission.OrdersView));
    }

    [Fact]
    public void The_cap_keeps_only_what_the_edition_grants_whatever_more_a_role_names()
    {
        RolePermission[] everything = [.. RolePermissions.Grantable];
        Assert.Equal(RolePermissions.Portal.Order(), TenantEditions.Caps(TenantEdition.CustomerPortal, everything).Order());
        Assert.Equal(TenantEditions.Ceiling(TenantEdition.Staffing).Order(), TenantEditions.Caps(TenantEdition.Staffing, everything).Order());

        Assert.Equal(
            RolePresets.CustomerAdmin.Permissions.Order(),
            TenantEditions.Caps(TenantEdition.CustomerPortal, [.. RolePresets.CustomerAdmin.Permissions, RolePermission.UsersCreate, RolePermission.CustomersView, RolePermission.PerformCoordination]).Order());
        Assert.Empty(TenantEditions.Caps(TenantEdition.Staffing, RolePresets.Orderer.Permissions));
    }
}
