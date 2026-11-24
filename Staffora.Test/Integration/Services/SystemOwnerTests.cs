using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class SystemOwnerTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public SystemOwnerTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_the_last_owners_holding_is_refused_while_removing_one_of_two_is_not()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var ownerHolding = await HoldingIdAsync(onboarded.OwnerId, onboarded.Roles.SystemOwner);

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            Assert.Equal(RoleHoldingService.LastOwnerKeepsTheRole, (await host.Get<RoleHoldingService>().RemoveUserFromRoleAsync(ownerHolding)).Error);
        }

        Assert.Equal([onboarded.OwnerId], await ActiveOwnersAsync(onboarded.TenantId));

        var second = await AppointOwnerAsync(onboarded, "Second");
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var removed = await host.Get<RoleHoldingService>().RemoveUserFromRoleAsync(await HoldingIdAsync(second, onboarded.Roles.SystemOwner));
            Assert.True(removed.IsSuccess, removed.Error);
        }

        Assert.Equal([onboarded.OwnerId], await ActiveOwnersAsync(onboarded.TenantId));

        var inactive = await AppointOwnerAsync(onboarded, "Inactive");
        await UserEdits.SetStateInTableAsync(this.database, inactive, UserState.Inactive, Ct);
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            Assert.Equal(RoleHoldingService.LastOwnerKeepsTheRole, (await host.Get<RoleHoldingService>().RemoveUserFromRoleAsync(ownerHolding)).Error);
        }
    }

    [Fact]
    public async Task Deactivating_the_last_active_owner_is_refused_by_the_Deactivate_step()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);

        var administrator = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Administrator", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, administrator, Ct, [.. RolePresets.SystemOwner.Permissions]);
        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(administrator)))
        {
            var refused = await host.Get<UserService>().StepUserAsync(onboarded.OwnerId, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.Contains(ApplicationUserWorkflowAttribute.LastOwnerReason, refused.Error, StringComparison.Ordinal);
            Assert.Contains(ApplicationUserWorkflowAttribute.OwnerByOwnerReason, refused.Error, StringComparison.Ordinal);

            var answers = await host.Get<WorkflowService>().GetAvailableTransitionsAsync<UserState, ApplicationUser>(onboarded.OwnerId, ApplicationUserWorkflowQueries.WithWorkflowGraph, Ct);
            Assert.Contains(ApplicationUserWorkflowAttribute.LastOwnerReason, answers.Value!.Single(answer => answer.Transition.Key == ApplicationUserWorkflowAttribute.DeactivateStep).BlockingReasons);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var own = await host.Get<UserService>().StepUserAsync(onboarded.OwnerId, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.Contains(ApplicationUserWorkflowAttribute.LastOwnerReason, own.Error, StringComparison.Ordinal);
        }

        Assert.Equal([onboarded.OwnerId], await ActiveOwnersAsync(onboarded.TenantId));

        var second = await AppointOwnerAsync(onboarded, "Second");
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var deactivated = await host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.True(deactivated.IsSuccess, deactivated.Error);
        }

        Assert.Equal([onboarded.OwnerId], await ActiveOwnersAsync(onboarded.TenantId));
    }

    [Fact]
    public async Task Two_owners_removing_each_others_holding_or_deactivating_each_other_at_once_leave_one_active_owner()
    {
        for (var round = 0; round < 3; round++)
        {
            var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
            var second = await AppointOwnerAsync(onboarded, "Second");
            var firstHolding = await HoldingIdAsync(onboarded.OwnerId, onboarded.Roles.SystemOwner);
            var secondHolding = await HoldingIdAsync(second, onboarded.Roles.SystemOwner);
            await using (var first = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
            await using (var other = ServiceHost.Create(this.database, onboarded.RuntimeOf(second)))
            {
                var removed = await Task.WhenAll(
                    Task.Run(() => first.Get<RoleHoldingService>().RemoveUserFromRoleAsync(secondHolding), Ct),
                    Task.Run(() => other.Get<RoleHoldingService>().RemoveUserFromRoleAsync(firstHolding), Ct));
                Assert.Single(removed, result => result.IsSuccess);
            }

            Assert.Single(await ActiveOwnersAsync(onboarded.TenantId));

            onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
            second = await AppointOwnerAsync(onboarded, "Second");
            var rendezvous = new Rendezvous(2, TimeSpan.FromSeconds(2));
            await using (var first = ServiceHost.Create(this.database, onboarded.OwnerRuntime(), services => services.AddScoped<IStepPreparation>(_ => rendezvous)))
            await using (var other = ServiceHost.Create(this.database, onboarded.RuntimeOf(second), services => services.AddScoped<IStepPreparation>(_ => rendezvous)))
            {
                var deactivated = await Task.WhenAll(
                    Task.Run(() => first.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.DeactivateStep), Ct),
                    Task.Run(() => other.Get<UserService>().StepUserAsync(onboarded.OwnerId, ApplicationUserWorkflowAttribute.DeactivateStep), Ct));
                Assert.Single(deactivated, result => result.IsSuccess);

                var refused = Assert.Single(deactivated, result => !result.IsSuccess).Error ?? string.Empty;
                Assert.True(refused.Contains(ApplicationUserWorkflowAttribute.OwnerByOwnerReason, StringComparison.Ordinal) || refused.Contains(ApplicationUserWorkflowAttribute.LastOwnerReason, StringComparison.Ordinal) || refused.Contains("permission", StringComparison.Ordinal), refused);
            }

            Assert.Single(await ActiveOwnersAsync(onboarded.TenantId));
        }
    }

    [Fact]
    public async Task The_System_owner_role_is_neither_edited_nor_deleted_even_by_an_owner_and_its_dialog_says_why()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var role = onboarded.Roles.SystemOwner;
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var roles = host.Get<TenantRoleService>();
            var holdings = host.Get<RoleHoldingService>();
            Assert.Contains(TenantRoleWorkflowAttribute.NotLockedReason, (await roles.EditRoleAsync(role, edited => edited.Name = "Owner", Ct)).Error, StringComparison.Ordinal);
            Assert.Contains(TenantRoleWorkflowAttribute.NotLockedReason, (await roles.EditRoleAsync(role, edited => edited.Description = "Changed", Ct)).Error, StringComparison.Ordinal);
            Assert.Contains(TenantRoleWorkflowAttribute.NotLockedReason, (await roles.EditRoleAsync(role, edited => edited.Revoke(RolePermission.UsersEdit), Ct)).Error, StringComparison.Ordinal);
            Assert.Contains(TenantRoleWorkflowAttribute.NotLockedReason, (await roles.EditRoleAsync(role, edited => edited.Grant(RolePermission.ViewOwnSchedule), Ct)).Error, StringComparison.Ordinal);
            Assert.Contains(TenantRoleWorkflowAttribute.NotLockedReason, (await roles.DeleteRoleAsync(role, Ct)).Error, StringComparison.Ordinal);

            var options = (await roles.GetTenantRoleEditorOptionsAsync(role, Ct)).Value!;
            Assert.Equal((true, TenantRoleService.SystemOwnerRoleReadOnly, true), (options.FieldsLocked, options.LockedReason, options.CanManageHolders));
            var answers = (await host.Get<WorkflowService>().GetAvailableTransitionsAsync<TenantRoleState, TenantRole>(role, TenantRoleWorkflowQueries.WithWorkflowGraph, Ct)).Value!;
            Assert.All(answers, answer => Assert.Contains(TenantRoleWorkflowAttribute.NotLockedReason, answer.BlockingReasons));
        }

        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read the locked role");
        var stored = await system.TenantRoles.AsNoTracking().Include(row => row.Permissions).SingleAsync(row => row.Id == role, Ct);
        Assert.Equal((RolePresets.SystemOwner.Name, RolePresets.SystemOwner.Description, TenantRoleState.Active, true), (stored.Name, stored.Description!, stored.State, stored.IsSystemOwner));
        Assert.Equal(RolePresets.SystemOwner.Permissions.Order(), stored.Permissions.Select(row => row.Permission).Order());

        system.TenantRoles.Add(new TenantRole { TenantId = onboarded.TenantId, Name = "Second owner role", State = TenantRoleState.Active, IsSystemOwner = true, CreatedById = SystemAccount.IdFor(onboarded.TenantId) });
        await Assert.ThrowsAsync<DbUpdateException>(() => system.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_last_owner_keeps_every_permission_after_deactivating_or_archiving_the_unit_of_another_holding_and_editing_another_role()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, onboarded.Tenant, Ct);
        Guid branch;
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            branch = await host.Get<OrganizationService>().CreateUnitIdAsync("North branch", OrganizationUnitType.Branch, rootId, Ct);
            var held = await host.Get<RoleHoldingService>().AssignUserToRoleAsync(onboarded.OwnerId, branch, onboarded.Roles.Administrator, onboarded.TenantId);
            Assert.True(held.IsSuccess, held.Error);
        }

        await AssertOwnerHoldsEverythingAsync(onboarded);
        foreach (var step in new[] { OrganizationUnitWorkflowAttribute.DeactivateStep, OrganizationUnitWorkflowAttribute.ArchiveInactiveStep })
        {
            await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
            {
                var moved = await host.Get<OrganizationService>().StepAsync(branch, step, Ct);
                Assert.True(moved.IsSuccess, moved.Error);
            }

            await AssertOwnerHoldsEverythingAsync(onboarded);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var roles = host.Get<TenantRoleService>();
            var holdings = host.Get<RoleHoldingService>();
            var emptied = await roles.EditRoleAsync(onboarded.Roles.Administrator, role => role.Permissions.Clear(), Ct);
            Assert.True(emptied.IsSuccess, emptied.Error);
            var removed = await holdings.RemoveUserFromRoleAsync(await HoldingIdAsync(onboarded.OwnerId, onboarded.Roles.Administrator));
            Assert.True(removed.IsSuccess, removed.Error);
        }

        await AssertOwnerHoldsEverythingAsync(onboarded);
    }

    [Fact]
    public async Task Only_a_system_owner_appoints_or_removes_one_and_only_on_the_Company_root()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, onboarded.Tenant, Ct);
        var candidate = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Candidate", Ct);
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Administrator", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, administrator, Ct, [.. RolePresets.SystemOwner.Permissions]);

        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(administrator)))
        {
            var tenants = host.Get<RoleHoldingService>();
            var choices = (await tenants.GetRoleHoldingChoicesAsync(onboarded.TenantId, Ct)).Value!;
            Assert.DoesNotContain(choices.Roles, role => role.Id == onboarded.Roles.SystemOwner);
            Assert.Contains(onboarded.Roles.SystemOwner, choices.RolesBeyondCeiling);
            Assert.Equal(RoleHoldingService.OnlyOwnersAppointOwners, (await tenants.AssignUserToRoleAsync(candidate, rootId, onboarded.Roles.SystemOwner, onboarded.TenantId)).Error);
            Assert.Equal(RoleHoldingService.OnlyOwnersAppointOwners, (await tenants.RemoveUserFromRoleAsync(await HoldingIdAsync(onboarded.OwnerId, onboarded.Roles.SystemOwner))).Error);
        }

        Guid branch;
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            branch = await host.Get<OrganizationService>().CreateUnitIdAsync("South branch", OrganizationUnitType.Branch, rootId, Ct);
            var tenants = host.Get<RoleHoldingService>();
            Assert.Contains((await tenants.GetRoleHoldingChoicesAsync(onboarded.TenantId, Ct)).Value!.Roles, role => role.Id == onboarded.Roles.SystemOwner);
            Assert.Equal(OrganizationStructurePolicy.SystemOwnerOnRootOnly, (await tenants.AssignUserToRoleAsync(candidate, branch, onboarded.Roles.SystemOwner, onboarded.TenantId)).Error);
            var appointed = await tenants.AssignUserToRoleAsync(candidate, rootId, onboarded.Roles.SystemOwner, onboarded.TenantId);
            Assert.True(appointed.IsSuccess, appointed.Error);
        }

        Assert.Equal(new[] { onboarded.OwnerId, candidate }.Order(), (await ActiveOwnersAsync(onboarded.TenantId)).Order());

        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: write an owner's holding off the root");
        system.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = onboarded.TenantId, UserId = administrator, OrganizationUnitId = branch, TenantRoleId = onboarded.Roles.SystemOwner, CreatedById = SystemAccount.IdFor(onboarded.TenantId) });
        var refused = await Assert.ThrowsAsync<OrganizationStructureException>(() => system.SaveChangesAsync(Ct));
        Assert.Equal(OrganizationStructurePolicy.SystemOwnerOnRootOnly, refused.Message);
    }

    [Fact]
    public async Task Only_a_system_owner_changes_another_owners_password_lockout_or_access()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var second = await AppointOwnerAsync(onboarded, "Second");
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Administrator", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, administrator, Ct, [.. RolePresets.SystemOwner.Permissions]);

        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(administrator)))
        {
            var users = host.Get<UserService>();
            foreach (var step in new[] { ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep })
            {
                Assert.Contains(ApplicationUserWorkflowAttribute.OwnerByOwnerReason, (await users.StepUserAsync(second, step)).Error, StringComparison.Ordinal);
            }

            Assert.Contains(ApplicationUserWorkflowAttribute.SignInNameWithinCeilingReason, (await users.SaveUserAsync(second, user => user.Email = user.UserName = $"taken.{Guid.NewGuid():N}@northwind.example")).Error, StringComparison.Ordinal);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var renewed = await host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.True(renewed.IsSuccess, renewed.Error);
        }
    }

    [Fact]
    public async Task A_system_owner_gives_themselves_a_new_password_while_an_administrator_who_is_no_owner_does_not()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Administrator", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, administrator, Ct, [.. RolePresets.SystemOwner.Permissions]);

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var renewed = await host.Get<UserService>().StepUserAsync(onboarded.OwnerId, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.True(renewed.IsSuccess, renewed.Error);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(administrator)))
        {
            var refused = await host.Get<UserService>().StepUserAsync(administrator, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.Contains(ApplicationUserWorkflowAttribute.NotOwnPassword, refused.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Only_a_system_owner_unlocks_or_reactivates_another_owner()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var second = await AppointOwnerAsync(onboarded, "Second");
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Administrator", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, administrator, Ct, [.. RolePresets.SystemOwner.Permissions]);

        await LockOutAsync(second);
        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(administrator)))
        {
            Assert.Contains(ApplicationUserWorkflowAttribute.OwnerByOwnerReason, (await host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.UnlockStep)).Error, StringComparison.Ordinal);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var unlocked = await host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.UnlockStep);
            Assert.True(unlocked.IsSuccess, unlocked.Error);
            var deactivated = await host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.True(deactivated.IsSuccess, deactivated.Error);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(administrator)))
        {
            Assert.Contains(ApplicationUserWorkflowAttribute.OwnerByOwnerReason, (await host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.ReactivateStep)).Error, StringComparison.Ordinal);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var reactivated = await host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.ReactivateStep);
            Assert.True(reactivated.IsSuccess, reactivated.Error);
        }

        Assert.Equal(new[] { onboarded.OwnerId, second }.Order(), (await ActiveOwnersAsync(onboarded.TenantId)).Order());
    }

    [Fact]
    public async Task A_RolesEdit_holder_without_the_Staff_roles_Personal_permissions_gives_the_Staff_role_and_a_user_administrator_gives_a_staff_member_a_new_password()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, onboarded.Tenant, Ct);
        var staff = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Staff", Ct);
        var roleEditor = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Editor", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, roleEditor, Ct, RolePermission.RolesView, RolePermission.RolesEdit);
        var userAdministrator = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "UserAdmin", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, userAdministrator, Ct, RolePermission.UsersView, RolePermission.UsersEdit);

        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(roleEditor)))
        {
            var held = await host.Get<PermissionService>().GetUserPermissionsAsync(Ct);
            Assert.Empty(held.Intersect(RolePermissions.Personal));
            Assert.NotEmpty(RolePermissions.Personal);
            var tenants = host.Get<RoleHoldingService>();
            Assert.Contains((await tenants.GetRoleHoldingChoicesAsync(onboarded.TenantId, Ct)).Value!.Roles, role => role.Id == onboarded.Roles.Staff);
            var given = await tenants.AssignUserToRoleAsync(staff, rootId, onboarded.Roles.Staff, onboarded.TenantId);
            Assert.True(given.IsSuccess, given.Error);
        }

        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(userAdministrator)))
        {
            var renewed = await host.Get<UserService>().StepUserAsync(staff, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.True(renewed.IsSuccess, renewed.Error);
            Assert.Contains("The new password is", renewed.Message, StringComparison.Ordinal);
        }

        var coordinator = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Coordinator", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, coordinator, Ct, RolePermission.SettingsView);
        await using (var host = ServiceHost.Create(this.database, onboarded.RuntimeOf(userAdministrator)))
        {
            Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, (await host.Get<UserService>().StepUserAsync(coordinator, ApplicationUserWorkflowAttribute.NewPasswordStep)).Error, StringComparison.Ordinal);
        }
    }

    private sealed class Rendezvous : IStepPreparation
    {
        private readonly int parties;
        private readonly TimeSpan wait;

        public Rendezvous(int parties, TimeSpan wait)
        {
            this.parties = parties;
            this.wait = wait;
        }

        private int arrived;
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Type EntityType => typeof(ApplicationUser);

        public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref this.arrived) >= this.parties)
            {
                this.all.TrySetResult();
            }

            await Task.WhenAny(this.all.Task, Task.Delay(this.wait, cancellationToken));
        }
    }

    private async Task<Guid> AppointOwnerAsync(OnboardedScenario onboarded, string name)
    {
        var user = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, name, Ct);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, onboarded.Tenant, Ct);
        await using var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime());
        var appointed = await host.Get<RoleHoldingService>().AssignUserToRoleAsync(user, rootId, onboarded.Roles.SystemOwner, onboarded.TenantId);
        Assert.True(appointed.IsSuccess, appointed.Error);
        return user;
    }

    private async Task AssertOwnerHoldsEverythingAsync(OnboardedScenario onboarded)
    {
        await using var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime());
        Assert.Equal(RolePresets.SystemOwner.Permissions.Order(), (await host.Get<PermissionService>().GetUserPermissionsAsync(Ct)).Order());
    }

    private async Task LockOutAsync(Guid userId)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.SingleAsync(row => row.Id == userId, Ct);
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
        context.NameEditor(user, SystemAccount.IdFor(user.TenantId));
        await context.SaveChangesAsync(Ct);
    }

    private async Task<Guid> HoldingIdAsync(Guid userId, Guid roleId)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: find a holding");
        return await system.OrganizationUnitUserRoles.Where(holding => holding.UserId == userId && holding.TenantRoleId == roleId).Select(holding => holding.Id).SingleAsync(Ct);
    }

    private async Task<List<Guid>> ActiveOwnersAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: list the active owners");
        return await SystemOwners.ActiveOwnerIds(system, tenantId).ToListAsync(Ct);
    }
}
