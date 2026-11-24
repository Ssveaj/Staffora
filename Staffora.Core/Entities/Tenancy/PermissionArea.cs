using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Tenancy;

public enum PermissionArea
{
    Tenant,
    Users,
    Roles,
    Organizations,
    OrganizationUnits,
    Settings,

    Personal,

    [Display(Name = "Staff")]
    Resources,

    [Display(Name = "Schedules and absence")]
    Scheduling,

    Customers,

    [Display(Name = "Jobs")]
    Jobs,

    Coordination,

    [Display(Name = "Customer portal")]
    Portal,
}

[AttributeUsage(AttributeTargets.Field)]
public class PermissionAreaAttribute : Attribute
{
    public PermissionArea Area { get; }

    public PermissionAreaAttribute(PermissionArea area)
    {
        Area = area;
    }
}
