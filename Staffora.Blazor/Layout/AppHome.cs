using Staffora.Core.Entities.Tenancy;

namespace Staffora.Blazor.Layout;

public static class AppHome
{
    public const string PlatformConsole = "/superadmin/overview";

    public const string SetupChecklist = "/tenant/management";

    public const string CoordinatorOverview = "/staffing/overview";

    public const string MyWork = "/me/work";

    public const string MyOverview = "/me/overview";

    public const string PortalHome = "/portal";

    public static IReadOnlyList<(RolePermission Gate, string Path)> ByStrength { get; } =
    [
        (RolePermission.TenantView, SetupChecklist),
        (RolePermission.ViewCoordination, CoordinatorOverview),
        (RolePermission.ViewOwnAssignments, MyWork),
    ];

    public static string For(bool platformConsole, IReadOnlySet<RolePermission> held) => For(platformConsole, TenantEdition.Staffing, held);

    public static string For(bool platformConsole, TenantEdition edition, IReadOnlySet<RolePermission> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (platformConsole)
        {
            return PlatformConsole;
        }

        if (edition == TenantEdition.CustomerPortal)
        {
            return PortalHome;
        }

        foreach (var (gate, path) in ByStrength)
        {
            if (held.Contains(gate))
            {
                return path;
            }
        }

        return MyOverview;
    }
}
