using Staffora.Blazor.Layout;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Test.Unit.Architecture;

public class AppHomeTests
{
    private static HashSet<RolePermission> Of(IEnumerable<RolePermission> permissions) => [.. permissions];

    [Fact]
    public void Each_role_preset_lands_on_its_own_home()
    {
        Assert.Equal(AppHome.SetupChecklist, AppHome.For(false, Of(RolePresets.Administrator.Permissions)));
        Assert.Equal(AppHome.SetupChecklist, AppHome.For(false, Of(RolePresets.SystemOwner.Permissions)));
        Assert.Equal(AppHome.CoordinatorOverview, AppHome.For(false, Of(RolePresets.Coordinator.Permissions)));
        Assert.Equal(AppHome.MyWork, AppHome.For(false, Of(RolePresets.Staff.Permissions)));
    }

    [Fact]
    public void A_user_holding_several_roles_lands_on_the_strongest_ones_home()
    {
        var staff = RolePresets.Staff.Permissions;
        Assert.Equal(AppHome.CoordinatorOverview, AppHome.For(false, Of(RolePresets.Coordinator.Permissions.Concat(staff))));
        Assert.Equal(AppHome.SetupChecklist, AppHome.For(false, Of(RolePresets.Administrator.Permissions.Concat(staff))));
        Assert.Equal(AppHome.SetupChecklist, AppHome.For(false, Of(RolePresets.Administrator.Permissions.Concat(RolePresets.Coordinator.Permissions).Concat(staff))));
    }

    [Fact]
    public void A_user_without_a_home_of_a_role_lands_on_their_own_overview()
    {
        Assert.Equal(AppHome.MyOverview, AppHome.For(false, new HashSet<RolePermission>()));
        Assert.Equal(AppHome.MyOverview, AppHome.For(false, Of([RolePermission.UsersView, RolePermission.OrganizationUnitsView, RolePermission.SettingsView])));
        Assert.Equal(AppHome.MyOverview, AppHome.For(false, Of([RolePermission.ViewOwnSchedule, RolePermission.ReportOwnAbsence])));
    }

    [Fact]
    public void The_platform_console_is_the_home_of_a_session_in_it_whatever_permissions_it_holds()
    {
        Assert.Equal(AppHome.PlatformConsole, AppHome.For(true, Of(Enum.GetValues<RolePermission>())));
        Assert.Equal(AppHome.PlatformConsole, AppHome.For(true, new HashSet<RolePermission>()));
    }

    [Fact]
    public void A_session_of_a_customer_portal_lands_on_the_portals_home_whatever_it_holds_and_the_console_stays_the_consoles()
    {
        Assert.Equal(AppHome.PortalHome, AppHome.For(false, TenantEdition.CustomerPortal, Of(RolePresets.CustomerAdmin.Permissions)));
        Assert.Equal(AppHome.PortalHome, AppHome.For(false, TenantEdition.CustomerPortal, Of(RolePresets.Orderer.Permissions)));
        Assert.Equal(AppHome.PortalHome, AppHome.For(false, TenantEdition.CustomerPortal, new HashSet<RolePermission>()));
        Assert.Equal(AppHome.PlatformConsole, AppHome.For(true, TenantEdition.CustomerPortal, Of(RolePresets.CustomerAdmin.Permissions)));
        Assert.Equal(AppHome.SetupChecklist, AppHome.For(false, TenantEdition.Staffing, Of(RolePresets.SystemOwner.Permissions)));

        var home = Assert.Single(AppNavigation.Portal.SelectMany(section => section.Items), item => item.Path == AppHome.PortalHome);
        Assert.Equal(RolePermissions.Portal, home.AnyOf);
    }

    [Fact]
    public void The_coordinators_overview_leads_the_staffing_section_and_the_workspace_keeps_its_own_link()
    {
        var staffing = Assert.Single(AppNavigation.Tenant, section => section.Title == "Staffing");
        Assert.Equal(("Overview", AppHome.CoordinatorOverview), (staffing.Items[0].Text, staffing.Items[0].Path));
        Assert.Equal([RolePermission.ViewCoordination], staffing.Items[0].AnyOf);
        Assert.Contains(staffing.Items, item => item.Text == "Coordination" && item.Path == "/coordination");

        var location = AppNavigation.Locate(AppNavigation.Tenant, AppHome.CoordinatorOverview);
        Assert.NotNull(location);
        Assert.Equal(("Staffing", "Overview", true), (location.Section.Title, location.Item.Text, location.Exact));
    }

    [Fact]
    public void Every_home_is_opened_by_exactly_the_permission_its_navigation_link_names()
    {
        var links = AppNavigation.Tenant.SelectMany(section => section.Items).ToDictionary(item => item.Path);
        Assert.Equal(3, AppHome.ByStrength.Count);
        Assert.All(AppHome.ByStrength, home => Assert.Equal([home.Gate], links[home.Path].AnyOf));
        Assert.Empty(links[AppHome.MyOverview].AnyOf);
        Assert.Contains(AppNavigation.Platform.SelectMany(section => section.Items), item => item.Path == AppHome.PlatformConsole);
    }
}
