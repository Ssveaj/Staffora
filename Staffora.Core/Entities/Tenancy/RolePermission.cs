using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Tenancy;

public enum RolePermission
{
    [Display(Name = "No Permissions", GroupName = "General")]
    None = 0,

    [PermissionArea(PermissionArea.Users)]
    [Display(Name = "View Users")]
    [Description("View user list and details")]
    UsersView = 1,

    [PermissionArea(PermissionArea.Tenant)]
    [Display(Name = "View Tenant")]
    [Description("View Tenant and details")]
    TenantView = 2,

    [PermissionArea(PermissionArea.Users)]
    [Display(Name = "Create Users")]
    [Description("Create new users")]
    UsersCreate = 3,

    [PermissionArea(PermissionArea.Users)]
    [Display(Name = "Edit Users")]
    [Description("Edit existing users")]
    UsersEdit = 4,

    [PermissionArea(PermissionArea.Users)]
    [Display(Name = "Delete Users")]
    [Description("Delete users")]
    UsersDelete = 5,

    [PermissionArea(PermissionArea.Roles)]
    [Display(Name = "View Roles")]
    [Description("View role list and details")]
    RolesView = 6,

    [PermissionArea(PermissionArea.Roles)]
    [Display(Name = "Create Roles")]
    [Description("Create new roles")]
    RolesCreate = 7,

    [PermissionArea(PermissionArea.Roles)]
    [Display(Name = "Edit Roles")]
    [Description("Edit existing roles")]
    RolesEdit = 8,

    [PermissionArea(PermissionArea.Roles)]
    [Display(Name = "Delete Roles")]
    [Description("Delete roles")]
    RolesDelete = 9,

    [Obsolete("The organization level is gone (ADR 0021); check OrganizationUnitsView.")]
    [PermissionArea(PermissionArea.Organizations)]
    [Display(Name = "View Organizations")]
    [Description("View organization list and details")]
    OrganizationsView = 10,

    [Obsolete("The organization level is gone (ADR 0021); check OrganizationUnitsCreate.")]
    [PermissionArea(PermissionArea.Organizations)]
    [Display(Name = "Create Organizations")]
    [Description("Create new organizations")]
    OrganizationsCreate = 11,

    [Obsolete("The organization level is gone (ADR 0021); check OrganizationUnitsEdit.")]
    [PermissionArea(PermissionArea.Organizations)]
    [Display(Name = "Edit Organizations")]
    [Description("Edit existing organizations")]
    OrganizationsEdit = 12,

    [Obsolete("The organization level is gone (ADR 0021); check OrganizationUnitsDelete.")]
    [PermissionArea(PermissionArea.Organizations)]
    [Display(Name = "Delete Organizations")]
    [Description("Delete organizations")]
    OrganizationsDelete = 13,

    [PermissionArea(PermissionArea.OrganizationUnits)]
    [Display(Name = "View Organization Units")]
    [Description("View organization unit list and details")]
    OrganizationUnitsView = 14,

    [PermissionArea(PermissionArea.OrganizationUnits)]
    [Display(Name = "Create Organization Units")]
    [Description("Create new organization units")]
    OrganizationUnitsCreate = 15,

    [PermissionArea(PermissionArea.OrganizationUnits)]
    [Display(Name = "Edit Organization Units")]
    [Description("Edit existing organization units")]
    OrganizationUnitsEdit = 16,

    [PermissionArea(PermissionArea.OrganizationUnits)]
    [Display(Name = "Delete Organization Units")]
    [Description("Delete organization units")]
    OrganizationUnitsDelete = 17,

    [PermissionArea(PermissionArea.Settings)]
    [Display(Name = "View Settings")]
    [Description("View tenant settings")]
    SettingsView = 18,

    [PermissionArea(PermissionArea.Settings)]
    [Display(Name = "Edit Settings")]
    [Description("Edit tenant settings")]
    SettingsEdit = 19,

    [PermissionArea(PermissionArea.Personal)]
    [Display(Name = "View Own Assignments")]
    [Description("See one's own assignments and requests")]
    ViewOwnAssignments = 20,

    [PermissionArea(PermissionArea.Personal)]
    [Display(Name = "Respond To Assignment")]
    [Description("Accept or decline one's own requests")]
    RespondToAssignment = 21,

    [PermissionArea(PermissionArea.Personal)]
    [Display(Name = "View Own Schedule")]
    [Description("See one's own schedule")]
    ViewOwnSchedule = 22,

    [PermissionArea(PermissionArea.Personal)]
    [Display(Name = "Report Own Absence")]
    [Description("Report one's own absence for approval")]
    ReportOwnAbsence = 23,

    [PermissionArea(PermissionArea.Resources)]
    [Display(Name = "View Staff")]
    [Description("See the staff members and the units they work for")]
    ResourcesView = 24,

    [PermissionArea(PermissionArea.Resources)]
    [Display(Name = "Manage Staff")]
    [Description("Change the staff members' records, and which roles make their holders staff members")]
    ResourcesManage = 25,

    [PermissionArea(PermissionArea.Resources)]
    [Display(Name = "Manage Qualifications")]
    [Description("Keep the qualification catalogue and give staff qualifications")]
    QualificationsManage = 26,

    [PermissionArea(PermissionArea.Scheduling)]
    [Display(Name = "Manage Work Patterns")]
    [Description("Create, publish and archive work patterns")]
    ManageWorkPatterns = 27,

    [PermissionArea(PermissionArea.Scheduling)]
    [Display(Name = "Assign Work Patterns")]
    [Description("Give staff members their work patterns")]
    AssignWorkPatterns = 28,

    [PermissionArea(PermissionArea.Scheduling)]
    [Display(Name = "View Staff Absence")]
    [Description("See when staff members are away")]
    ViewResourceAbsence = 29,

    [PermissionArea(PermissionArea.Scheduling)]
    [Display(Name = "Manage Staff Absence")]
    [Description("Register, change and cancel absence on a staff member's behalf")]
    ManageResourceAbsence = 30,

    [PermissionArea(PermissionArea.Scheduling)]
    [Display(Name = "Approve Staff Absence")]
    [Description("Approve or reject the absence staff members report")]
    ApproveResourceAbsence = 31,

    [PermissionArea(PermissionArea.Customers)]
    [Display(Name = "View Customers")]
    [Description("See the customers and their contacts")]
    CustomersView = 32,

    [PermissionArea(PermissionArea.Customers)]
    [Display(Name = "Manage Customers")]
    [Description("Create, change and delete customers and their contacts")]
    CustomersManage = 33,

    [PermissionArea(PermissionArea.Customers)]
    [Display(Name = "Enable Customer Portal")]
    [Description("Give a customer its own portal to order staff from")]
    CustomerPortalEnable = 34,

    [PermissionArea(PermissionArea.Jobs)]
    [Display(Name = "View Jobs")]
    [Description("See the jobs with their timetable and slots")]
    JobsView = 35,

    [PermissionArea(PermissionArea.Jobs)]
    [Display(Name = "Create Jobs")]
    [Description("Register new jobs for customers")]
    JobsCreate = 36,

    [PermissionArea(PermissionArea.Jobs)]
    [Display(Name = "Edit Jobs")]
    [Description("Change jobs, their timetable and slots")]
    JobsEdit = 37,

    [PermissionArea(PermissionArea.Jobs)]
    [Display(Name = "Delete Jobs")]
    [Description("Delete jobs that are not staffed yet")]
    JobsDelete = 38,

    [PermissionArea(PermissionArea.Jobs)]
    [Display(Name = "Cancel Jobs")]
    [Description("Cancel jobs, with everyone on them told")]
    JobsCancel = 39,

    [PermissionArea(PermissionArea.Coordination)]
    [Display(Name = "View Coordination")]
    [Description("See the coordination workspace and the candidates for a slot")]
    ViewCoordination = 40,

    [PermissionArea(PermissionArea.Coordination)]
    [Display(Name = "Perform Coordination")]
    [Description("Request and assign staff members, and confirm jobs")]
    PerformCoordination = 41,

    [PermissionArea(PermissionArea.Portal)]
    [Display(Name = "View Orders")]
    [Description("See the organization's orders and where each stands")]
    OrdersView = 42,

    [PermissionArea(PermissionArea.Portal)]
    [Display(Name = "Create Orders")]
    [Description("Write new orders and change them until they are sent")]
    OrdersCreate = 43,

    [PermissionArea(PermissionArea.Portal)]
    [Display(Name = "Send Orders")]
    [Description("Send orders to the staffing company")]
    OrdersSend = 44,

    [PermissionArea(PermissionArea.Portal)]
    [Display(Name = "Cancel Orders")]
    [Description("Cancel orders until the staffing company confirms them")]
    OrdersCancel = 45,

    [PermissionArea(PermissionArea.Portal)]
    [Display(Name = "Manage Customer Users")]
    [Description("Add the organization's orderers and take them out of use")]
    CustomerUsersManage = 46,

    [PermissionArea(PermissionArea.Settings)]
    [Display(Name = "Edit Services")]
    [Description("Keep the service catalogue: what the company sells, the qualifications each needs, and what the portal offers")]
    ServicesEdit = 47,
}
