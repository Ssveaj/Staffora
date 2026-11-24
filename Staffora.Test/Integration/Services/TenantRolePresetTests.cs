using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class TenantRolePresetTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantRolePresetTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_tenant_gets_the_exact_permission_set_of_each_preset_and_the_staff_flag_on_the_Staff_role_alone()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct);

        await using var context = this.database.CreateSystemContext();
        var roles = await context.TenantRoles.AsNoTracking()
            .Where(role => role.TenantId == onboarded.TenantId)
            .Select(role => new { role.Id, role.Name, role.IsSystemOwner, role.ApplyResourceMembershipToUser, Permissions = role.Permissions.Select(permission => permission.Permission).ToList(), Stamps = role.Permissions.Select(permission => permission.CreatedById).ToList() })
            .ToListAsync(Ct);

        Assert.Equal(4, roles.Count);
        var expected = new Dictionary<Guid, RolePreset>
        {
            [onboarded.Roles.SystemOwner] = RolePresets.SystemOwner,
            [onboarded.Roles.Administrator] = RolePresets.Administrator,
            [onboarded.Roles.Coordinator] = RolePresets.Coordinator,
            [onboarded.Roles.Staff] = RolePresets.Staff,
        };
        foreach (var role in roles)
        {
            var preset = expected[role.Id];
            Assert.Equal(preset.Name, role.Name);
            Assert.Equal(preset.Permissions.Order(), role.Permissions.Order());
            Assert.Equal(preset.ApplyResourceMembershipToUser, role.ApplyResourceMembershipToUser);
            Assert.All(role.Stamps, stamp => Assert.Equal(onboarded.Tenant.SystemAccountId, stamp));
        }

        var owner = roles.Single(role => role.IsSystemOwner);
        Assert.Equal(RolePermissions.Grantable.Where(permission => !RolePermissions.IsPersonal(permission) && !RolePermissions.IsPortal(permission)).Order(), owner.Permissions.Order());
        var coordinator = roles.Single(role => role.Id == onboarded.Roles.Coordinator);
        Assert.Equal(
            [RolePermission.UsersView, RolePermission.ResourcesView, RolePermission.ResourcesManage, RolePermission.QualificationsManage, RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns, RolePermission.ViewResourceAbsence, RolePermission.ManageResourceAbsence, RolePermission.ApproveResourceAbsence, RolePermission.CustomersView, RolePermission.CustomersManage, RolePermission.JobsView, RolePermission.JobsCreate, RolePermission.JobsEdit, RolePermission.JobsDelete, RolePermission.JobsCancel, RolePermission.ViewCoordination, RolePermission.PerformCoordination],
            coordinator.Permissions.Order());
        var staff = roles.Single(role => role.Id == onboarded.Roles.Staff);
        Assert.Equal([RolePermission.ViewOwnAssignments, RolePermission.RespondToAssignment, RolePermission.ViewOwnSchedule, RolePermission.ReportOwnAbsence], staff.Permissions.Order());
        Assert.Equal([onboarded.Roles.Staff], roles.Where(role => role.ApplyResourceMembershipToUser).Select(role => role.Id));
        var administrator = roles.Single(role => role.Id == onboarded.Roles.Administrator);
        Assert.Equal(
            [RolePermission.ResourcesView, RolePermission.QualificationsManage, RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns, RolePermission.ServicesEdit],
            administrator.Permissions.Where(permission => (int)permission >= (int)RolePermission.ResourcesView).Order());
        Assert.DoesNotContain(RolePermission.ResourcesManage, administrator.Permissions);
    }
}
