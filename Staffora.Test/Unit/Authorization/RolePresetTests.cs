using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Test.Unit.Authorization;

public class RolePresetTests
{
    public static TheoryData<PermissionArea, RolePermission[]> StaffingAreas => new()
    {
        { PermissionArea.Personal, [RolePermission.ViewOwnAssignments, RolePermission.RespondToAssignment, RolePermission.ViewOwnSchedule, RolePermission.ReportOwnAbsence] },
        { PermissionArea.Resources, [RolePermission.ResourcesView, RolePermission.ResourcesManage, RolePermission.QualificationsManage] },
        { PermissionArea.Scheduling, [RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns, RolePermission.ViewResourceAbsence, RolePermission.ManageResourceAbsence, RolePermission.ApproveResourceAbsence] },
        { PermissionArea.Customers, [RolePermission.CustomersView, RolePermission.CustomersManage, RolePermission.CustomerPortalEnable] },
        { PermissionArea.Jobs, [RolePermission.JobsView, RolePermission.JobsCreate, RolePermission.JobsEdit, RolePermission.JobsDelete, RolePermission.JobsCancel] },
        { PermissionArea.Coordination, [RolePermission.ViewCoordination, RolePermission.PerformCoordination] },
        { PermissionArea.Portal, [RolePermission.OrdersView, RolePermission.OrdersCreate, RolePermission.OrdersSend, RolePermission.OrdersCancel, RolePermission.CustomerUsersManage] },
    };

    [Theory]
    [MemberData(nameof(StaffingAreas))]
    public void Each_staffing_area_holds_exactly_the_permissions_of_the_plan(PermissionArea area, RolePermission[] permissions)
    {
        Assert.Equal(permissions, RolePermissions.Grantable.Where(permission => AreaOf(permission) == area));
    }

    [Fact]
    public void Every_grantable_permission_has_an_area_and_a_name_the_role_editor_shows()
    {
        Assert.All(RolePermissions.Grantable, permission =>
        {
            Assert.NotNull(AreaOf(permission));
            Assert.False(string.IsNullOrWhiteSpace(Field(permission).GetCustomAttribute<DisplayAttribute>()?.Name), permission.ToString());
        });
    }

    [Fact]
    public void The_System_owner_preset_holds_every_grantable_permission_outside_own_work_and_the_portal()
    {
        var expected = Enum.GetValues<RolePermission>()
            .Where(permission => permission != RolePermission.None && Field(permission).GetCustomAttribute<ObsoleteAttribute>() is null && AreaOf(permission) is not (PermissionArea.Personal or PermissionArea.Portal))
            .ToHashSet();

        Assert.Equal(expected.Order(), RolePresets.SystemOwner.Permissions.Order());
        Assert.Contains(RolePermission.PerformCoordination, RolePresets.SystemOwner.Permissions);
        Assert.DoesNotContain(RolePresets.SystemOwner.Permissions, RolePermissions.IsPersonal);
        Assert.DoesNotContain(RolePresets.SystemOwner.Permissions, RolePermissions.IsPortal);
        Assert.Equal(TenantEditions.Ceiling(TenantEdition.Staffing).Where(permission => !RolePermissions.IsPersonal(permission)), RolePresets.SystemOwner.Permissions);
        Assert.False(RolePresets.SystemOwner.ApplyResourceMembershipToUser);
    }

    [Fact]
    public void The_Coordinator_preset_holds_the_staffing_work_and_the_users_to_look_at_and_nothing_else()
    {
        RolePermission[] expected =
        [
            RolePermission.UsersView,
            RolePermission.ResourcesView, RolePermission.ResourcesManage, RolePermission.QualificationsManage,
            RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns, RolePermission.ViewResourceAbsence, RolePermission.ManageResourceAbsence, RolePermission.ApproveResourceAbsence,
            RolePermission.CustomersView, RolePermission.CustomersManage,
            RolePermission.JobsView, RolePermission.JobsCreate, RolePermission.JobsEdit, RolePermission.JobsDelete, RolePermission.JobsCancel,
            RolePermission.ViewCoordination, RolePermission.PerformCoordination,
        ];

        Assert.Equal(expected.Order(), RolePresets.Coordinator.Permissions.Order());
        Assert.False(RolePresets.Coordinator.ApplyResourceMembershipToUser);
    }

    [Fact]
    public void The_Staff_preset_holds_own_work_alone_and_makes_its_holders_staff_members()
    {
        Assert.Equal([RolePermission.ViewOwnAssignments, RolePermission.RespondToAssignment, RolePermission.ViewOwnSchedule, RolePermission.ReportOwnAbsence], RolePresets.Staff.Permissions);
        Assert.True(RolePresets.Staff.ApplyResourceMembershipToUser);
        Assert.Equal([RolePresets.Staff], RolePresets.Editable.Concat([RolePresets.SystemOwner]).Where(preset => preset.ApplyResourceMembershipToUser));
    }

    [Fact]
    public void The_Administrator_preset_holds_the_tenant_its_people_and_settings_and_setting_up_the_staff()
    {
        RolePermission[] expected =
        [
            RolePermission.TenantView,
            RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit, RolePermission.UsersDelete,
            RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesEdit, RolePermission.RolesDelete,
            RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.OrganizationUnitsDelete,
            RolePermission.SettingsView, RolePermission.SettingsEdit, RolePermission.ServicesEdit,
            RolePermission.ResourcesView, RolePermission.QualificationsManage, RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns,
        ];

        Assert.Equal(expected.Order(), RolePresets.Administrator.Permissions.Order());
        Assert.False(RolePresets.Administrator.ApplyResourceMembershipToUser);
    }

    [Fact]
    public void The_portal_presets_hold_the_portal_alone_the_customer_administrator_all_of_it_and_the_orderer_no_user_administration()
    {
        Assert.Equal([RolePresets.CustomerAdmin, RolePresets.Orderer], RolePresets.Portal);
        Assert.Equal(
            [RolePermission.OrdersView, RolePermission.OrdersCreate, RolePermission.OrdersSend, RolePermission.OrdersCancel, RolePermission.CustomerUsersManage],
            RolePresets.CustomerAdmin.Permissions);
        Assert.Equal(
            [RolePermission.OrdersView, RolePermission.OrdersCreate, RolePermission.OrdersSend, RolePermission.OrdersCancel],
            RolePresets.Orderer.Permissions);
        Assert.All(RolePresets.Portal, preset =>
        {
            Assert.All(preset.Permissions, permission => Assert.True(TenantEditions.Allows(TenantEdition.CustomerPortal, permission), permission.ToString()));
            Assert.False(preset.ApplyResourceMembershipToUser);
        });

        Assert.All(RolePresets.Editable.Concat([RolePresets.SystemOwner]), preset => Assert.DoesNotContain(preset.Permissions, RolePermissions.IsPortal));
    }

    private static FieldInfo Field(RolePermission permission) => typeof(RolePermission).GetField(permission.ToString())!;

    private static PermissionArea? AreaOf(RolePermission permission) => Field(permission).GetCustomAttribute<PermissionAreaAttribute>()?.Area;
}
