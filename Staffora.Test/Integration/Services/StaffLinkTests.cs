using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class StaffLinkTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public StaffLinkTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Giving_the_Staff_role_on_a_team_puts_them_in_service_and_in_that_team_and_removing_it_takes_them_out()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var kim = await scenario.AddPersonAsync(this.database, "Kim", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();

        Assert.Empty(await StaffScenario.RecordsOfAsync(this.database, kim, Ct));
        Assert.False(await InServiceAsync(kim));

        var given = await holdings.AssignUserToRoleAsync(kim, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        var record = await StaffScenario.RecordOfAsync(this.database, kim, Ct);
        Assert.Equal(ResourceState.Active, record.State);
        Assert.Equal([scenario.TeamId], record.Memberships.Select(membership => membership.OrganizationUnitId));
        Assert.True(await InServiceAsync(kim));

        var removed = await holdings.RemoveUserFromRoleAsync(given.Value!.Id);
        Assert.True(removed.IsSuccess, removed.Error);
        record = await StaffScenario.RecordOfAsync(this.database, kim, Ct);
        Assert.Equal(ResourceState.Inactive, record.State);
        Assert.Empty(record.Memberships);
        Assert.False(await InServiceAsync(kim));
        var history = await StaffScenario.HistoryOfAsync(this.database, record.Id, Ct);
        Assert.Equal([ResourceWorkflowAttribute.ActivateStep, ResourceWorkflowAttribute.DeactivateStep], history.Select(entry => entry.TransitionKey));
        Assert.All(history, entry => Assert.Equal(scenario.Onboarded.OwnerId, entry.ActorId));
    }

    [Fact]
    public async Task A_second_staff_record_for_the_same_user_is_refused()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var lee = await scenario.AddPersonAsync(this.database, "Lee", Ct);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            var roles = host.Get<TenantRoleService>();
            var holdings = host.Get<RoleHoldingService>();
            Assert.True((await holdings.AssignUserToRoleAsync(lee, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);

            Assert.True((await holdings.AssignUserToRoleAsync(lee, scenario.RootId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        var record = await StaffScenario.RecordOfAsync(this.database, lee, Ct);
        Assert.Equal(new[] { scenario.RootId, scenario.TeamId }.Order(), record.Memberships.Select(membership => membership.OrganizationUnitId).Order());

        await using var context = this.database.CreateContext(scenario.Onboarded.OwnerRuntime());
        context.Resources.Add(new Resource { UserId = lee });
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        Assert.Contains("IX_Resources_UserId", refused.InnerException!.Message, StringComparison.Ordinal);
        Assert.Single(await StaffScenario.RecordsOfAsync(this.database, lee, Ct));
    }

    [Fact]
    public async Task Deactivating_a_staff_member_takes_them_out_of_service_and_reactivating_puts_them_back_in_the_same_step()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var max = await scenario.AddPersonAsync(this.database, "Max", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(max, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        var users = host.Get<UserService>();

        var deactivated = await users.StepUserAsync(max, ApplicationUserWorkflowAttribute.DeactivateStep);
        Assert.True(deactivated.IsSuccess, deactivated.Error);
        var record = await StaffScenario.RecordOfAsync(this.database, max, Ct);
        Assert.Equal(ResourceState.Inactive, record.State);

        Assert.Equal([scenario.TeamId], record.Memberships.Select(membership => membership.OrganizationUnitId));
        Assert.False(await InServiceAsync(max));

        var reactivated = await users.StepUserAsync(max, ApplicationUserWorkflowAttribute.ReactivateStep);
        Assert.True(reactivated.IsSuccess, reactivated.Error);
        Assert.Equal(ResourceState.Active, (await StaffScenario.RecordOfAsync(this.database, max, Ct)).State);
        Assert.True(await InServiceAsync(max));
        Assert.Equal(
            [ResourceWorkflowAttribute.ActivateStep, ResourceWorkflowAttribute.DeactivateStep, ResourceWorkflowAttribute.ReactivateStep],
            (await StaffScenario.HistoryOfAsync(this.database, record.Id, Ct)).Select(entry => entry.TransitionKey));

        Assert.True((await users.SaveUserAsync(max, user => user.PhoneNumber = "+46 70 000 00 00")).IsSuccess);
        Assert.Equal(3, (await StaffScenario.HistoryOfAsync(this.database, record.Id, Ct)).Count);
    }

    [Fact]
    public async Task Turning_a_roles_staff_flag_off_and_on_moves_its_holders_and_only_someone_who_manages_staff_turns_it()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var ana = await scenario.AddPersonAsync(this.database, "Ana", Ct);
        var bo = await scenario.AddPersonAsync(this.database, "Bo", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();

        var created = await roles.CreateAsync(RoleSteps.Draft("Tutor", RolePermission.ViewOwnSchedule), Ct);
        Assert.True(created.IsSuccess, created.Error);
        var tutor = created.Value;
        Assert.True((await holdings.AssignUserToRoleAsync(ana, scenario.TeamId, tutor, scenario.TenantId)).IsSuccess);
        Assert.True((await holdings.AssignUserToRoleAsync(bo, scenario.RootId, tutor, scenario.TenantId)).IsSuccess);
        Assert.Empty(await StaffScenario.RecordsOfAsync(this.database, ana, Ct));

        var editor = await scenario.AddPersonAsync(this.database, "Editor", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, editor, Ct, RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesEdit, RolePermission.ViewOwnSchedule);
        await using (var editorHost = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(editor)))
        {
            var refused = await editorHost.Get<TenantRoleService>().EditRoleAsync(tutor, role => role.ApplyResourceMembershipToUser = true, Ct);
            Assert.Contains(TenantRoleWorkflowAttribute.ResourceFlagReason, refused.Error, StringComparison.Ordinal);

            Assert.True((await editorHost.Get<TenantRoleService>().EditRoleAsync(tutor, role => role.Description = "Helps out", Ct)).IsSuccess);
            Assert.True((await editorHost.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Assistant", RolePermission.ViewOwnSchedule), Ct)).IsSuccess);
            var flagged = RoleSteps.Draft("Assistant staff member", RolePermission.ViewOwnSchedule);
            flagged.ApplyResourceMembershipToUser = true;
            Assert.Equal(TenantRoleService.ResourceFlagRequiresResourcesManage, (await editorHost.Get<TenantRoleService>().CreateAsync(flagged, Ct)).Error);
        }

        Assert.Empty(await StaffScenario.RecordsOfAsync(this.database, ana, Ct));

        Assert.True((await roles.EditRoleAsync(tutor, role => role.ApplyResourceMembershipToUser = true, Ct)).IsSuccess);
        var anaRecord = await StaffScenario.RecordOfAsync(this.database, ana, Ct);
        var boRecord = await StaffScenario.RecordOfAsync(this.database, bo, Ct);
        Assert.Equal((ResourceState.Active, scenario.TeamId), (anaRecord.State, Assert.Single(anaRecord.Memberships).OrganizationUnitId));
        Assert.Equal((ResourceState.Active, scenario.RootId), (boRecord.State, Assert.Single(boRecord.Memberships).OrganizationUnitId));

        Assert.True((await roles.EditRoleAsync(tutor, role => role.Name = "Tutor of record", Ct)).IsSuccess);
        Assert.Single(await StaffScenario.HistoryOfAsync(this.database, anaRecord.Id, Ct));

        Assert.True((await roles.EditRoleAsync(tutor, role => role.ApplyResourceMembershipToUser = false, Ct)).IsSuccess);
        anaRecord = await StaffScenario.RecordOfAsync(this.database, ana, Ct);
        Assert.Equal((ResourceState.Inactive, 0), (anaRecord.State, anaRecord.Memberships.Count));
        Assert.Equal(ResourceState.Inactive, (await StaffScenario.RecordOfAsync(this.database, bo, Ct)).State);
    }

    [Fact]
    public async Task A_unit_taken_out_of_use_takes_its_staff_members_membership_and_a_staff_member_with_no_unit_left_out_of_service()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var eva = await scenario.AddPersonAsync(this.database, "Eva", Ct);
        var finn = await scenario.AddPersonAsync(this.database, "Finn", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();
        var units = host.Get<OrganizationService>();

        Assert.True((await holdings.AssignUserToRoleAsync(eva, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        Assert.True((await holdings.AssignUserToRoleAsync(finn, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        Assert.True((await holdings.AssignUserToRoleAsync(finn, scenario.RootId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);

        var deactivated = await units.StepAsync(scenario.TeamId, OrganizationUnitWorkflowAttribute.DeactivateStep, Ct);
        Assert.True(deactivated.IsSuccess, deactivated.Error);
        var evaRecord = await StaffScenario.RecordOfAsync(this.database, eva, Ct);
        var finnRecord = await StaffScenario.RecordOfAsync(this.database, finn, Ct);
        Assert.Equal((ResourceState.Inactive, 0), (evaRecord.State, evaRecord.Memberships.Count));
        Assert.Equal((ResourceState.Active, scenario.RootId), (finnRecord.State, Assert.Single(finnRecord.Memberships).OrganizationUnitId));
        Assert.False(await InServiceAsync(eva));

        var reactivated = await units.StepAsync(scenario.TeamId, OrganizationUnitWorkflowAttribute.ReactivateStep, Ct);
        Assert.True(reactivated.IsSuccess, reactivated.Error);
        evaRecord = await StaffScenario.RecordOfAsync(this.database, eva, Ct);
        Assert.Equal((ResourceState.Active, scenario.TeamId), (evaRecord.State, Assert.Single(evaRecord.Memberships).OrganizationUnitId));
        Assert.Equal(2, (await StaffScenario.RecordOfAsync(this.database, finn, Ct)).Memberships.Count);
    }

    [Fact]
    public async Task Deactivating_a_unit_takes_the_staff_members_of_the_units_below_it_out_with_it()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var jo = await scenario.AddPersonAsync(this.database, "Jo", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var units = host.Get<OrganizationService>();

        var branch = await units.CreateUnitIdAsync("Branch East", OrganizationUnitType.Branch, scenario.RootId, Ct);
        var team = await units.CreateUnitIdAsync("Team East", OrganizationUnitType.Team, branch, Ct);
        Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(jo, team, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        Assert.Equal((ResourceState.Active, team), ((await StaffScenario.RecordOfAsync(this.database, jo, Ct)).State, Assert.Single((await StaffScenario.RecordOfAsync(this.database, jo, Ct)).Memberships).OrganizationUnitId));

        var deactivated = await units.StepAsync(branch, OrganizationUnitWorkflowAttribute.DeactivateStep, Ct);
        Assert.True(deactivated.IsSuccess, deactivated.Error);
        await using (var context = this.database.CreateSystemContext())
        {
            Assert.Equal(OrganizationUnitState.Inactive, await context.OrganizationUnits.Where(unit => unit.Id == team).Select(unit => unit.State).SingleAsync(Ct));
        }

        var record = await StaffScenario.RecordOfAsync(this.database, jo, Ct);
        Assert.Equal((ResourceState.Inactive, 0), (record.State, record.Memberships.Count));
        Assert.False(await InServiceAsync(jo));
        Assert.Equal([ResourceWorkflowAttribute.ActivateStep, ResourceWorkflowAttribute.DeactivateStep], (await StaffScenario.HistoryOfAsync(this.database, record.Id, Ct)).Select(entry => entry.TransitionKey));
    }

    [Fact]
    public async Task The_role_create_step_itself_refuses_a_staff_role_to_someone_who_does_not_manage_staff()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var editor = await scenario.AddPersonAsync(this.database, "Editor", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, editor, Ct, RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesEdit, RolePermission.ViewOwnSchedule);

        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(editor)))
        {
            var workflows = host.Get<Staffora.Services.Workflow.WorkflowService>();
            var flagged = new TenantRole { TenantId = scenario.TenantId, Name = "Sneaky", State = TenantRoleState.Create, ApplyResourceMembershipToUser = true };
            var refused = await workflows.CreateAndTransitionAsync<TenantRoleState, TenantRole>(flagged, TenantRoleWorkflowAttribute.CreateStep, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Equal((false, TenantRoleWorkflowAttribute.ResourceFlagReason), (refused.IsSuccess, refused.Error));

            var plain = new TenantRole { TenantId = scenario.TenantId, Name = "Plain", State = TenantRoleState.Create };
            var created = await workflows.CreateAndTransitionAsync<TenantRoleState, TenantRole>(plain, TenantRoleWorkflowAttribute.CreateStep, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.True(created.IsSuccess, created.Error);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            Assert.False(await context.TenantRoles.AnyAsync(role => role.TenantId == scenario.TenantId && role.Name == "Sneaky", Ct));
        }

        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            var flagged = new TenantRole { TenantId = scenario.TenantId, Name = "Owner's staff members", State = TenantRoleState.Create, ApplyResourceMembershipToUser = true };
            var created = await owner.Get<Staffora.Services.Workflow.WorkflowService>().CreateAndTransitionAsync<TenantRoleState, TenantRole>(flagged, TenantRoleWorkflowAttribute.CreateStep, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.True(created.IsSuccess, created.Error);
        }
    }

    [Fact]
    public async Task A_staff_role_held_by_a_user_who_is_not_active_makes_a_record_in_Registration_which_reactivation_puts_in_service()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var gil = await scenario.AddPersonAsync(this.database, "Gil", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var roles = host.Get<TenantRoleService>();
        var holdings = host.Get<RoleHoldingService>();
        var created = await roles.CreateAsync(RoleSteps.Draft("Substitute", RolePermission.ViewOwnSchedule), Ct);
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await holdings.AssignUserToRoleAsync(gil, scenario.TeamId, created.Value, scenario.TenantId)).IsSuccess);
        Assert.True((await host.Get<UserService>().StepUserAsync(gil, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);

        Assert.True((await roles.EditRoleAsync(created.Value, role => role.ApplyResourceMembershipToUser = true, Ct)).IsSuccess);
        var record = await StaffScenario.RecordOfAsync(this.database, gil, Ct);
        Assert.Equal((ResourceState.Registration, scenario.TeamId), (record.State, Assert.Single(record.Memberships).OrganizationUnitId));
        Assert.False(await InServiceAsync(gil));

        Assert.True((await host.Get<UserService>().StepUserAsync(gil, ApplicationUserWorkflowAttribute.ReactivateStep)).IsSuccess);
        Assert.Equal(ResourceState.Active, (await StaffScenario.RecordOfAsync(this.database, gil, Ct)).State);
        Assert.Equal([ResourceWorkflowAttribute.ActivateStep], (await StaffScenario.HistoryOfAsync(this.database, record.Id, Ct)).Select(entry => entry.TransitionKey));
    }

    [Fact]
    public async Task A_record_step_that_fails_takes_the_write_that_called_for_it_back_with_it()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var hal = await scenario.AddPersonAsync(this.database, "Hal", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var tenants = host.Get<RoleHoldingService>();
        var given = await tenants.AssignUserToRoleAsync(hal, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        var record = await StaffScenario.RecordOfAsync(this.database, hal, Ct);

        await HoldAsync(record.Id, held: true);
        Assert.False((await tenants.RemoveUserFromRoleAsync(given.Value!.Id)).IsSuccess);
        Assert.Equal(1, await HoldingsAsync(hal));
        Assert.Equal(ResourceState.Active, (await StaffScenario.RecordOfAsync(this.database, hal, Ct)).State);

        Assert.False((await host.Get<UserService>().StepUserAsync(hal, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);
        await using (var context = this.database.CreateSystemContext())
        {
            Assert.Equal(UserState.Active, await context.Users.Where(user => user.Id == hal).Select(user => user.State).SingleAsync(Ct));
        }

        await HoldAsync(record.Id, held: false);
        Assert.True((await tenants.RemoveUserFromRoleAsync(given.Value.Id)).IsSuccess);
        Assert.Equal(ResourceState.Inactive, (await StaffScenario.RecordOfAsync(this.database, hal, Ct)).State);
    }

    [Fact]
    public async Task A_unit_staff_members_belong_to_is_not_deleted_while_a_unit_without_them_is()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct);
        var ivy = await scenario.AddPersonAsync(this.database, "Ivy", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var units = host.Get<OrganizationService>();
        Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(ivy, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        var record = await StaffScenario.RecordOfAsync(this.database, ivy, Ct);

        var empty = await units.CreateUnitIdAsync("Team South", OrganizationUnitType.Team, scenario.RootId, Ct);
        await using (var context = this.database.CreateSystemContext())
        {
            context.ResourceUnitMemberships.Add(new ResourceUnitMembership { TenantId = scenario.TenantId, ResourceId = record.Id, OrganizationUnitId = empty, CreatedById = scenario.Onboarded.OwnerId });
            await context.SaveChangesAsync(Ct);
        }

        var refused = await units.StepAsync(empty, OrganizationUnitWorkflowAttribute.DeleteStep, Ct);
        Assert.Contains(ResourceMembersBlockUnitDelete.Reason, refused.Error, StringComparison.Ordinal);

        await using (var context = this.database.CreateSystemContext())
        {
            context.ResourceUnitMemberships.Remove(await context.ResourceUnitMemberships.SingleAsync(membership => membership.OrganizationUnitId == empty, Ct));
            await context.SaveChangesAsync(Ct);
        }

        Assert.True((await units.StepAsync(empty, OrganizationUnitWorkflowAttribute.DeleteStep, Ct)).IsSuccess);
    }

    private async Task<bool> InServiceAsync(Guid userId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Resources.Where(resource => resource.UserId == userId).AnyAsync(StaffServiceRules.InService, Ct);
    }

    private async Task HoldAsync(Guid resourceId, bool held)
    {
        await using var context = this.database.CreateSystemContext();
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE Resources SET IsInTransition = {held} WHERE Id = {resourceId}", Ct));
    }

    private async Task<int> HoldingsAsync(Guid userId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.OrganizationUnitUserRoles.CountAsync(holding => holding.UserId == userId, Ct);
    }
}
