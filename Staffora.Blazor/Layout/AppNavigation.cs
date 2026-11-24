using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;

namespace Staffora.Blazor.Layout;

public sealed record NavLinkItem(string Text, string Icon, string Path, IReadOnlyList<RolePermission> AnyOf, IReadOnlyList<RolePermission>? NoneOf = null)
{
    public bool IsVisibleTo(IReadOnlySet<RolePermission> held) =>
        (AnyOf.Count == 0 || AnyOf.Any(held.Contains)) && !(NoneOf ?? []).Any(held.Contains);
}

public sealed record NavSection(string Title, IReadOnlyList<NavLinkItem> Items);

public sealed record NavLocation(NavSection Section, NavLinkItem Item, bool Exact);

public static class AppNavigation
{
    public static IReadOnlyList<NavSection> Tenant { get; } =
    [
        new("My work",
        [
            new("My overview", "dashboard", AppHome.MyOverview, [], [.. AppHome.ByStrength.Select(home => home.Gate)]),
            new("My work", "work", AppHome.MyWork, [RolePermission.ViewOwnAssignments]),
            new("My assignments", "assignment", "/me/assignments", [RolePermission.ViewOwnAssignments]),
            new("My schedule", "calendar_today", "/me/schedule", [RolePermission.ViewOwnSchedule]),
        ]),
        new("Staffing",
        [
            new("Overview", "insights", AppHome.CoordinatorOverview, [RolePermission.ViewCoordination], [RolePermission.TenantView]),
            new("Coordination", "hub", "/coordination", [RolePermission.ViewCoordination]),
            new("Staff", "badge", "/staffing/staff", [RolePermission.ResourcesView]),
            new("Qualifications", "workspace_premium", "/staffing/qualifications", [RolePermission.ResourcesView, RolePermission.QualificationsManage]),
            new("Work patterns", "calendar_month", "/staffing/work-patterns", [RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns]),
            new("Absence", "event_busy", "/staffing/absence", [RolePermission.ViewResourceAbsence, RolePermission.ManageResourceAbsence, RolePermission.ApproveResourceAbsence, RolePermission.ReportOwnAbsence]),
            new("Customers", "storefront", "/staffing/customers", [RolePermission.CustomersView, RolePermission.CustomersManage]),
            new("Jobs", "work", "/staffing/jobs", [RolePermission.JobsView, RolePermission.JobsCreate, RolePermission.JobsEdit, RolePermission.JobsDelete, RolePermission.JobsCancel, RolePermission.ViewCoordination, RolePermission.PerformCoordination]),
        ]),
        new("Organization",
        [
            new("Overview", "dashboard_customize", "/tenant/management", [RolePermission.TenantView]),
            new("Units", "account_tree", "/tenant/management/organization-units", [RolePermission.OrganizationUnitsView]),
            new("Users", "people", "/tenant/management/users", [RolePermission.UsersView]),
            new("Roles & permissions", "admin_panel_settings", "/tenant/management/roles", [RolePermission.RolesView]),
            new("Settings", "settings", "/tenant/management/settings", [RolePermission.SettingsView]),
        ]),
    ];

    public static IReadOnlyList<NavSection> Portal { get; } =
    [
        new("Portal",
        [
            new("Home", "storefront", AppHome.PortalHome, RolePermissions.Portal),
            new("My orders", "list_alt", "/portal/orders", [RolePermission.OrdersView]),
            new("New order", "post_add", "/portal/orders/new", [RolePermission.OrdersCreate]),
        ]),
    ];

    public static IReadOnlyList<NavSection> Platform { get; } =
    [
        new("Platform",
        [
            new("Overview", "space_dashboard", "/superadmin/overview", []),
            new("Tenants", "apartment", "/superadmin/tenants", []),
            new("Platform settings", "tune", "/superadmin/settings", []),
            new("Platform health", "monitor_heart", "/superadmin/errorlog", []),
        ]),
    ];

    public static IReadOnlyList<NavSection> VisibleTo(IReadOnlyList<NavSection> sections, IReadOnlySet<RolePermission> held) =>
        [.. sections
            .Select(section => section with { Items = [.. section.Items.Where(item => item.IsVisibleTo(held))] })
            .Where(section => section.Items.Count > 0)];

    public static async Task<IReadOnlyList<NavSection>> ForSessionAsync(RuntimeSnapshot session, PermissionService permissions)
    {
        if (session.EffectiveUserId is null)
        {
            return [];
        }

        if (session.HasPlatformReach())
        {
            return Platform;
        }

        var held = await permissions.GetUserPermissionsAsync(session);
        var portal = await permissions.GetEditionAsync(session) == TenantEdition.CustomerPortal;
        return VisibleTo(portal ? Portal : Tenant, held);
    }

    public static NavLocation? Locate(IReadOnlyList<NavSection> sections, string path)
    {
        var page = "/" + path.Split('?', '#')[0].Trim('/');
        NavLocation? under = null;
        foreach (var section in sections)
        {
            foreach (var item in section.Items)
            {
                if (string.Equals(item.Path, page, StringComparison.OrdinalIgnoreCase))
                {
                    return new NavLocation(section, item, Exact: true);
                }

                if (page.StartsWith(item.Path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                    && (under is null || item.Path.Length > under.Item.Path.Length))
                {
                    under = new NavLocation(section, item, Exact: false);
                }
            }
        }

        return under;
    }
}
