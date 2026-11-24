using System.Reflection;

namespace Staffora.Core.Entities.Tenancy;

public static class RolePermissions
{
    private static readonly HashSet<RolePermission> obsolete = Enum.GetValues<RolePermission>()
        .Where(permission => typeof(RolePermission).GetField(permission.ToString())?.IsDefined(typeof(ObsoleteAttribute)) == true)
        .ToHashSet();

    public static IReadOnlyList<RolePermission> Grantable { get; } = Enum.GetValues<RolePermission>()
        .Where(permission => permission != RolePermission.None && !obsolete.Contains(permission))
        .ToArray();

    public static bool IsObsolete(RolePermission permission) => obsolete.Contains(permission);

    private static readonly HashSet<RolePermission> personal = Enum.GetValues<RolePermission>()
        .Where(permission => typeof(RolePermission).GetField(permission.ToString())?.GetCustomAttribute<PermissionAreaAttribute>()?.Area == PermissionArea.Personal)
        .ToHashSet();

    public static IReadOnlyList<RolePermission> Personal { get; } = Grantable.Where(personal.Contains).ToArray();

    public static bool IsPersonal(RolePermission permission) => personal.Contains(permission);

    private static readonly HashSet<RolePermission> portal = Enum.GetValues<RolePermission>()
        .Where(permission => typeof(RolePermission).GetField(permission.ToString())?.GetCustomAttribute<PermissionAreaAttribute>()?.Area == PermissionArea.Portal)
        .ToHashSet();

    public static IReadOnlyList<RolePermission> Portal { get; } = Grantable.Where(portal.Contains).ToArray();

    public static bool IsPortal(RolePermission permission) => portal.Contains(permission);
}
