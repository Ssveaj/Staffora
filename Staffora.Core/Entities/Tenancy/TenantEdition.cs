namespace Staffora.Core.Entities.Tenancy;

public enum TenantEdition
{
    Staffing = 0,

    CustomerPortal = 1,
}

public static class TenantEditions
{
    public static TenantEdition Of(Guid? parentTenantId) => parentTenantId is null ? TenantEdition.Staffing : TenantEdition.CustomerPortal;

    public static bool Allows(TenantEdition edition, RolePermission permission) => edition switch
    {
        TenantEdition.Staffing => !RolePermissions.IsPortal(permission),
        TenantEdition.CustomerPortal => RolePermissions.IsPortal(permission),
        _ => false,
    };

    public static IReadOnlyList<RolePermission> Ceiling(TenantEdition edition) =>
        [.. RolePermissions.Grantable.Where(permission => Allows(edition, permission))];

    public static HashSet<RolePermission> Caps(TenantEdition edition, IEnumerable<RolePermission> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return [.. held.Where(permission => Allows(edition, permission))];
    }
}
