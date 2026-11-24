namespace Staffora.Core.Entities.Tenancy;

public static class RolePresets
{
    public static RolePreset SystemOwner { get; } = new(
        "System owner",
        "Everything in the organization except one's own work. Appoints other system owners. Locked: it cannot be edited or deleted.",
        [.. RolePermissions.Grantable.Where(permission => !RolePermissions.IsPersonal(permission) && !RolePermissions.IsPortal(permission))]);

    public static RolePreset Administrator { get; } = new(
        "Administrator",
        "Users, roles, organization units and settings. Sets staff members up with qualifications and work patterns.",
        [
            .. RolePermissions.Grantable.Where(permission => AreaOf(permission) is PermissionArea.Tenant or PermissionArea.Users or PermissionArea.Roles or PermissionArea.OrganizationUnits or PermissionArea.Settings),
            RolePermission.ResourcesView,
            RolePermission.QualificationsManage,
            RolePermission.ManageWorkPatterns,
            RolePermission.AssignWorkPatterns,
        ]);

    public static RolePreset Coordinator { get; } = new(
        "Coordinator",
        "Customers, jobs and their slots, staff members' schedules and absence. Assigns staff members.",
        [
            RolePermission.UsersView,
            RolePermission.ResourcesView,
            RolePermission.ResourcesManage,
            RolePermission.QualificationsManage,
            RolePermission.ManageWorkPatterns,
            RolePermission.AssignWorkPatterns,
            RolePermission.ViewResourceAbsence,
            RolePermission.ManageResourceAbsence,
            RolePermission.ApproveResourceAbsence,
            RolePermission.CustomersView,
            RolePermission.CustomersManage,
            RolePermission.JobsView,
            RolePermission.JobsCreate,
            RolePermission.JobsEdit,
            RolePermission.JobsDelete,
            RolePermission.JobsCancel,
            RolePermission.ViewCoordination,
            RolePermission.PerformCoordination,
        ]);

    public static RolePreset Staff { get; } = new(
        "Staff",
        "Their own schedule, absence and assignments. Answers their own requests.",
        RolePermissions.Personal)
    {
        ApplyResourceMembershipToUser = true,
    };

    public static IReadOnlyList<RolePreset> Editable { get; } = [Administrator, Coordinator, Staff];

    public static RolePreset CustomerAdmin { get; } = new(
        "Customer administrator",
        "Orders staff for the organization's jobs, follows and cancels its orders, and manages its orderers.",
        RolePermissions.Portal);

    public static RolePreset Orderer { get; } = new(
        "Orderer",
        "Orders staff for the organization's jobs, and follows and cancels its orders.",
        [.. RolePermissions.Portal.Where(permission => permission != RolePermission.CustomerUsersManage)]);

    public static IReadOnlyList<RolePreset> Portal { get; } = [CustomerAdmin, Orderer];

    private static PermissionArea? AreaOf(RolePermission permission) =>
        (typeof(RolePermission).GetField(permission.ToString())?.GetCustomAttributes(typeof(PermissionAreaAttribute), false).FirstOrDefault() as PermissionAreaAttribute)?.Area;
}

public sealed record RolePreset(string Name, string Description, IReadOnlyList<RolePermission> Permissions)
{
    public bool ApplyResourceMembershipToUser { get; init; }
}
