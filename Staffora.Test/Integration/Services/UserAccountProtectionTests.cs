using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class UserAccountProtectionTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public UserAccountProtectionTests(RealDatabase database)
    {
        this.database = database;
    }

    public static TheoryData<string> RowWritingSteps =>
    [
        ApplicationUserWorkflowAttribute.NewPasswordStep,
        ApplicationUserWorkflowAttribute.DeactivateStep,
        ApplicationUserWorkflowAttribute.UnlockStep,
    ];

    [Theory]
    [MemberData(nameof(RowWritingSteps))]
    public async Task A_stale_Identity_copy_saved_after_a_step_is_refused_and_the_steps_values_stay(string step)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, target) = await AdministratorAndTargetAsync(cancellationToken);
        if (step == ApplicationUserWorkflowAttribute.UnlockStep)
        {
            await LockOutAsync(target, cancellationToken);
        }

        await using var signIn = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var identity = signIn.Get<UserManager<ApplicationUser>>();
        var stale = (await identity.FindByIdAsync(target.ToString()))!;
        await using (var admin = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var stepped = await admin.Get<UserService>().StepUserAsync(target, step);
            Assert.True(stepped.IsSuccess, stepped.Error);
        }

        var afterStep = await StoredAsync(target, cancellationToken);
        Assert.NotEqual(stale.ConcurrencyStamp, afterStep.ConcurrencyStamp);

        stale.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
        stale.AccessFailedCount = 5;
        var written = await identity.UpdateAsync(stale);
        Assert.False(written.Succeeded);
        Assert.Equal(nameof(IdentityErrorDescriber.ConcurrencyFailure), Assert.Single(written.Errors).Code);
        var stored = await StoredAsync(target, cancellationToken);
        Assert.Equal(
            (afterStep.PasswordHash, afterStep.SecurityStamp, afterStep.LockoutEnd, afterStep.AccessFailedCount, afterStep.State),
            (stored.PasswordHash, stored.SecurityStamp, stored.LockoutEnd, stored.AccessFailedCount, stored.State));

        Assert.True((await identity.AccessFailedAsync(stale)).Succeeded);
        var counted = await StoredAsync(target, cancellationToken);
        Assert.Equal(
            (afterStep.PasswordHash, afterStep.SecurityStamp, afterStep.ConcurrencyStamp, afterStep.LockoutEnd, afterStep.AccessFailedCount + 1, afterStep.State),
            (counted.PasswordHash, counted.SecurityStamp, counted.ConcurrencyStamp, counted.LockoutEnd, counted.AccessFailedCount, counted.State));
    }

    [Theory]
    [MemberData(nameof(RowWritingSteps))]
    public async Task A_sign_in_with_a_copy_read_before_a_step_fails_cleanly_and_writes_nothing_but_the_failure(string step)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, target) = await AdministratorAndTargetAsync(cancellationToken);
        if (step == ApplicationUserWorkflowAttribute.UnlockStep)
        {
            await LockOutAsync(target, cancellationToken);
        }

        const string password = "Correct-Horse-7";
        await using (var setup = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var manager = setup.Get<UserManager<ApplicationUser>>();
            Assert.True((await manager.AddPasswordAsync((await manager.FindByIdAsync(target.ToString()))!, password)).Succeeded);
        }

        await using var signIn = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var stale = (await signIn.Get<UserManager<ApplicationUser>>().FindByIdAsync(target.ToString()))!;
        await using (var admin = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.True((await admin.Get<UserService>().StepUserAsync(target, step)).IsSuccess);
        }

        var afterStep = await StoredAsync(target, cancellationToken);

        if (step == ApplicationUserWorkflowAttribute.DeactivateStep)
        {
            Assert.True((await signIn.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(stale, password, lockoutOnFailure: false)).IsNotAllowed);
        }

        var result = await signIn.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(stale, "Wrong-password-1", lockoutOnFailure: true);
        Assert.False(result.Succeeded);
        var stored = await StoredAsync(target, cancellationToken);
        var failures = step == ApplicationUserWorkflowAttribute.UnlockStep ? 0 : 1;
        Assert.Equal((afterStep.ConcurrencyStamp, afterStep.PasswordHash, afterStep.AccessFailedCount + failures), (stored.ConcurrencyStamp, stored.PasswordHash, stored.AccessFailedCount));
    }

    [Fact]
    public async Task A_new_password_removes_the_authenticator_and_ends_every_earlier_cookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, target) = await AdministratorAndTargetAsync(cancellationToken);
        await GiveSecondFactorsAsync(target, cancellationToken);
        var cookie = await CookieOfAsync(target);
        Assert.True((await SessionCircuit.ResolveAsync(this.database, cookie)).IsInitialized);

        await using (var admin = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var reset = await admin.Get<UserService>().StepUserAsync(target, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.True(reset.IsSuccess, reset.Error);
            Assert.Contains("The new password is ", reset.Message, StringComparison.Ordinal);
        }

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.UserTokens.AnyAsync(token => token.UserId == target, cancellationToken));
        }

        Assert.False((await StoredAsync(target, cancellationToken)).TwoFactorEnabled);

        Assert.False((await SessionCircuit.ResolveAsync(this.database, cookie)).IsInitialized);
        Assert.True((await SessionCircuit.ResolveAsync(this.database, await CookieOfAsync(target))).IsInitialized);
    }

    [Fact]
    public async Task A_cookie_from_before_a_deactivation_opens_no_circuit_even_once_the_user_is_active_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, target) = await AdministratorAndTargetAsync(cancellationToken);
        var cookie = await CookieOfAsync(target);
        await using (var admin = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.True((await admin.Get<UserService>().StepUserAsync(target, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);
            Assert.True((await admin.Get<UserService>().StepUserAsync(target, ApplicationUserWorkflowAttribute.ReactivateStep)).IsSuccess);
        }

        Assert.Equal(UserState.Active, (await StoredAsync(target, cancellationToken)).State);
        Assert.False((await SessionCircuit.ResolveAsync(this.database, cookie)).IsInitialized);
    }

    [Fact]
    public async Task A_user_administrator_changes_no_credentials_or_lifecycle_of_an_account_that_holds_more_and_those_of_a_weaker_one_as_before()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Administrator", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, administrator, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit, RolePermission.RolesEdit, RolePermission.SettingsEdit);
        var weaker = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Weaker", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, weaker, cancellationToken, RolePermission.UsersView);
        var owner = scenario.A.OwnerId;
        var before = await StoredAsync(owner, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator));
        var users = host.Get<UserService>();

        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, (await users.StepUserAsync(owner, ApplicationUserWorkflowAttribute.NewPasswordStep)).Error, StringComparison.Ordinal);
        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, (await users.StepUserAsync(owner, ApplicationUserWorkflowAttribute.DeactivateStep)).Error, StringComparison.Ordinal);
        await LockOutAsync(owner, cancellationToken);
        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, (await users.StepUserAsync(owner, ApplicationUserWorkflowAttribute.UnlockStep)).Error, StringComparison.Ordinal);
        var email = $"taken.over.{Guid.NewGuid():N}@company-a.example";
        Assert.Contains(ApplicationUserWorkflowAttribute.SignInNameWithinCeilingReason, (await users.SaveUserAsync(owner, user => user.Email = email)).Error, StringComparison.Ordinal);
        Assert.Contains(ApplicationUserWorkflowAttribute.SignInNameWithinCeilingReason, (await users.SaveUserAsync(owner, user => user.UserName = email)).Error, StringComparison.Ordinal);
        var stored = await StoredAsync(owner, cancellationToken);
        Assert.Equal((before.PasswordHash, before.SecurityStamp, UserState.Active, before.Email, before.UserName), (stored.PasswordHash, stored.SecurityStamp, stored.State, stored.Email, stored.UserName));
        Assert.NotNull(stored.LockoutEnd);

        var offered = (await host.Get<WorkflowService>().GetAvailableTransitionsAsync<UserState, ApplicationUser>(owner, ApplicationUserWorkflowQueries.WithWorkflowGraph, cancellationToken)).Value!;
        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, offered.Single(step => step.Transition.Key == ApplicationUserWorkflowAttribute.NewPasswordStep).BlockingReasons);
        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, offered.Single(step => step.Transition.Key == ApplicationUserWorkflowAttribute.DeactivateStep).BlockingReasons);

        Assert.True((await users.SaveUserAsync(owner, user => user.PhoneNumber = "+46 70 222 22 22")).IsSuccess);

        await UserEdits.SetStateInTableAsync(this.database, owner, UserState.Inactive, cancellationToken);
        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, (await users.StepUserAsync(owner, ApplicationUserWorkflowAttribute.ReactivateStep)).Error, StringComparison.Ordinal);
        Assert.Equal(UserState.Inactive, (await StoredAsync(owner, cancellationToken)).State);

        var renamed = $"weaker.renamed.{Guid.NewGuid():N}@company-a.example";
        Assert.True((await users.SaveUserAsync(weaker, user => user.Email = renamed)).IsSuccess);
        Assert.True((await users.StepUserAsync(weaker, ApplicationUserWorkflowAttribute.NewPasswordStep)).IsSuccess);
        Assert.True((await users.StepUserAsync(weaker, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);
        Assert.True((await users.StepUserAsync(weaker, ApplicationUserWorkflowAttribute.ReactivateStep)).IsSuccess);
        Assert.Equal(renamed, (await StoredAsync(weaker, cancellationToken)).Email);
    }

    [Fact]
    public async Task A_role_held_on_an_inactive_unit_still_counts_towards_the_ceiling_on_credentials_and_lifecycle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Administrator", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, administrator, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var target = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Dormant", cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        await using (var context = this.database.CreateSystemContext())
        {
            var unit = new OrganizationUnit { TenantId = scenario.A.TenantId, ParentId = rootId, Name = TwoTenantScenario.UniqueName("Closed branch"), UnitType = OrganizationUnitType.Branch, State = OrganizationUnitState.Active, CreatedById = scenario.A.OwnerId };
            var role = new TenantRole { TenantId = scenario.A.TenantId, Name = TwoTenantScenario.UniqueName("Strong"), State = TenantRoleState.Active, CreatedById = scenario.A.OwnerId, Permissions = [new TenantRolePermission { TenantId = scenario.A.TenantId, Permission = RolePermission.UsersEdit, CreatedById = scenario.A.OwnerId }, new TenantRolePermission { TenantId = scenario.A.TenantId, Permission = RolePermission.SettingsEdit, CreatedById = scenario.A.OwnerId }] };
            context.AddRange(unit, role);
            context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = scenario.A.TenantId, UserId = target, OrganizationUnit = unit, TenantRole = role, CreatedById = scenario.A.OwnerId });
            await context.SaveChangesAsync(cancellationToken);
            context.NameEditor(unit, scenario.A.OwnerId);
            using (context.BeginTransitionSaveScope([unit], "test: the unit taken out of use"))
            {
                unit.State = OrganizationUnitState.Inactive;
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        await using (var asTarget = ServiceHost.Create(this.database, scenario.A.RuntimeOf(target)))
        {
            Assert.False(await asTarget.Get<PermissionService>().HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken));
        }

        var before = await StoredAsync(target, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(administrator));
        var users = host.Get<UserService>();
        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, (await users.StepUserAsync(target, ApplicationUserWorkflowAttribute.NewPasswordStep)).Error, StringComparison.Ordinal);
        Assert.Contains(ApplicationUserWorkflowAttribute.WithinCeilingReason, (await users.StepUserAsync(target, ApplicationUserWorkflowAttribute.DeactivateStep)).Error, StringComparison.Ordinal);
        var stored = await StoredAsync(target, cancellationToken);
        Assert.Equal((before.PasswordHash, before.SecurityStamp, UserState.Active), (stored.PasswordHash, stored.SecurityStamp, stored.State));
    }

    [Fact]
    public async Task A_cookie_without_a_security_stamp_opens_no_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, target) = await AdministratorAndTargetAsync(cancellationToken);
        var issued = await CookieOfAsync(target);
        Assert.True((await SessionCircuit.ResolveAsIssuedAsync(this.database, issued)).IsInitialized);

        var identity = new ClaimsIdentity(issued.Claims.Where(claim => claim.Type != UserSessionService.SecurityStampClaimType), issued.Identity!.AuthenticationType);
        Assert.False((await SessionCircuit.ResolveAsIssuedAsync(this.database, new ClaimsPrincipal(identity))).IsInitialized);
    }

    [Fact]
    public async Task No_session_changes_a_platform_owners_credentials_or_access_the_platform_console_included()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var second = await TwoTenantScenario.AddUserAsync(this.database, platform, "SecondOwner", cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, second, cancellationToken);
        var before = await StoredAsync(second, cancellationToken);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var users = console.Get<UserService>();

        Assert.Contains(ApplicationUserWorkflowAttribute.NotAPlatformOwnerReason, (await users.StepUserAsync(second, ApplicationUserWorkflowAttribute.NewPasswordStep)).Error, StringComparison.Ordinal);
        Assert.Contains(ApplicationUserWorkflowAttribute.NotAPlatformOwnerReason, (await users.StepUserAsync(second, ApplicationUserWorkflowAttribute.DeactivateStep)).Error, StringComparison.Ordinal);
        await LockOutAsync(second, cancellationToken);
        Assert.Contains(ApplicationUserWorkflowAttribute.NotAPlatformOwnerReason, (await users.StepUserAsync(second, ApplicationUserWorkflowAttribute.UnlockStep)).Error, StringComparison.Ordinal);
        await UserEdits.SetStateInTableAsync(this.database, second, UserState.Inactive, cancellationToken);
        Assert.Contains(ApplicationUserWorkflowAttribute.NotAPlatformOwnerReason, (await users.StepUserAsync(second, ApplicationUserWorkflowAttribute.ReactivateStep)).Error, StringComparison.Ordinal);

        var stored = await StoredAsync(second, cancellationToken);
        Assert.Equal((before.PasswordHash, before.SecurityStamp), (stored.PasswordHash, stored.SecurityStamp));
    }

    [Fact]
    public async Task The_platform_console_reactivates_no_tenants_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Member", cancellationToken);
        await UserEdits.SetStateInTableAsync(this.database, member, UserState.Inactive, cancellationToken);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());

        var reactivated = await console.Get<UserService>().StepUserAsync(member, ApplicationUserWorkflowAttribute.ReactivateStep);

        Assert.False(reactivated.IsSuccess);
        Assert.Contains("Only a user of this organization changes its users", reactivated.Error, StringComparison.Ordinal);
        Assert.Equal(UserState.Inactive, (await StoredAsync(member, cancellationToken)).State);
    }

    [Fact]
    public async Task A_tenants_system_account_gets_no_new_password_past_the_user_service()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var before = await StoredAsync(scenario.A.SystemAccountId, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());

        var past = await host.Get<WorkflowService>().TransitionEntityAsync<UserState, ApplicationUser>(scenario.A.SystemAccountId, ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);

        Assert.False(past.IsSuccess);
        Assert.Contains("system account", past.Error, StringComparison.Ordinal);
        Assert.Equal(before.PasswordHash, (await StoredAsync(scenario.A.SystemAccountId, cancellationToken)).PasswordHash);
    }

    [Fact]
    public async Task A_user_created_past_the_service_as_a_system_account_or_with_sign_in_state_set_ahead_is_refused_and_nothing_is_stored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersCreate);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var workflows = host.Get<WorkflowService>();

        foreach (var setAhead in new Action<ApplicationUser>[]
        {
            user => user.IsSystemAccount = true,
            user => user.TwoFactorEnabled = true,
            user => user.LockoutEnd = DateTimeOffset.UtcNow.AddYears(10),
            user => user.AccessFailedCount = 3,
            user => (user.IsSystemAccount, user.TwoFactorEnabled, user.LockoutEnabled, user.PasswordHash) = (true, true, false, "forged-hash"),
        })
        {
            var email = $"ghost.{Guid.NewGuid():N}@company-a.example";
            var user = new ApplicationUser
            {
                TenantId = scenario.A.TenantId,
                State = UserState.Create,
                FirstName = "Ghost",
                LastName = "Account",
                Email = email,
                UserName = email,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant(),
                EmailConfirmed = true,
                LockoutEnabled = true,
            };
            setAhead(user);

            var created = await workflows.CreateAndTransitionAsync<UserState, ApplicationUser>(user, ApplicationUserWorkflowAttribute.CreateStep, ApplicationUserWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);

            Assert.False(created.IsSuccess);
            Assert.Equal(ApplicationUserWorkflowAttribute.OrdinaryNewAccountReason, created.Error);
            await using var check = this.database.CreateSystemContext();
            Assert.False(await check.Users.AnyAsync(stored => stored.Email == email, cancellationToken));
        }

        var plainEmail = $"plain.{Guid.NewGuid():N}@company-a.example";
        var plain = await workflows.CreateAndTransitionAsync<UserState, ApplicationUser>(new ApplicationUser
        {
            TenantId = scenario.A.TenantId,
            State = UserState.Create,
            FirstName = "Plain",
            LastName = "Account",
            Email = plainEmail,
            UserName = plainEmail,
            NormalizedEmail = plainEmail.ToUpperInvariant(),
            NormalizedUserName = plainEmail.ToUpperInvariant(),
            EmailConfirmed = true,
            LockoutEnabled = true,
        }, ApplicationUserWorkflowAttribute.CreateStep, ApplicationUserWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.True(plain.IsSuccess, plain.Error);
        Assert.False((await StoredAsync(plain.Value, cancellationToken)).IsSystemAccount);
    }

    [Fact]
    public async Task Identity_creates_no_user_from_a_session_with_a_signed_in_user_whatever_it_holds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var nobody = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "NoPermissions", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersCreate);
        foreach (var runtime in new[] { scenario.A.RuntimeOf(nobody), scenario.A.Runtime() })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            var email = $"bypass.{Guid.NewGuid():N}@company-a.example";
            var created = await host.Get<UserManager<ApplicationUser>>().CreateAsync(new ApplicationUser { FirstName = "By", LastName = "Pass", Email = email, UserName = email, TenantId = scenario.A.TenantId, State = UserState.Active }, "Password123!x");

            Assert.Equal(ApplicationUserStore.CreateOutsideSetupRefused, Assert.Single(created.Errors).Description);
            await using var check = this.database.CreateSystemContext();
            Assert.False(await check.Users.AnyAsync(user => user.Email == email, cancellationToken));
        }
    }

    [Fact]
    public async Task A_user_written_without_a_state_signs_in_nowhere_and_holds_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var email = $"stateless.{Guid.NewGuid():N}@company-a.example";
        Guid id;
        await using (var setup = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var user = new ApplicationUser { FirstName = "No", LastName = "State", Email = email, UserName = email, TenantId = scenario.A.TenantId };
            Assert.True((await setup.Get<UserManager<ApplicationUser>>().CreateAsync(user, "Password123!x")).Succeeded);
            id = user.Id;
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, id, cancellationToken, RolePermission.UsersView);
        Assert.Equal(UserState.Create, (await StoredAsync(id, cancellationToken)).State);

        await using var signIn = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var found = (await signIn.Get<UserManager<ApplicationUser>>().FindByIdAsync(id.ToString()))!;
        Assert.False((await signIn.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(found, "Password123!x", lockoutOnFailure: false)).Succeeded);
        Assert.False((await SessionCircuit.ResolveAsync(this.database, id)).IsInitialized);
        await using var asThem = ServiceHost.Create(this.database, scenario.A.RuntimeOf(id));
        Assert.False(await asThem.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersView, cancellationToken));
    }

    [Fact]
    public async Task An_inactive_users_roles_grant_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Member", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, member, cancellationToken, RolePermission.UsersView);
        await using (var active = ServiceHost.Create(this.database, scenario.A.RuntimeOf(member)))
        {
            Assert.True(await active.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersView, cancellationToken));
        }

        await UserEdits.SetStateInTableAsync(this.database, member, UserState.Inactive, cancellationToken);
        await using var inactive = ServiceHost.Create(this.database, scenario.A.RuntimeOf(member));
        Assert.False(await inactive.Get<PermissionService>().HasPermissionAsync(RolePermission.UsersView, cancellationToken));
    }

    [Fact]
    public async Task A_new_password_cancelled_before_its_commit_stores_nothing_and_is_dropped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, target) = await AdministratorAndTargetAsync(cancellationToken);
        var before = await StoredAsync(target, cancellationToken);
        using var cancellation = new CancellationTokenSource();
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), interceptors: [new CancelAtPasswordSave(cancellation, afterTheSave: false)]);
        var users = host.Get<UserService>();
        var updater = (await users.LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;

        Result? answer = null;
        try
        {
            answer = await users.UpdateApplicationUserAndTransitionAsync(updater, ApplicationUserWorkflowAttribute.NewPasswordStep, null, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(answer?.IsSuccess ?? false);
        Assert.Equal((before.PasswordHash, before.SecurityStamp), ((await StoredAsync(target, cancellationToken)).PasswordHash, (await StoredAsync(target, cancellationToken)).SecurityStamp));
        Assert.Null(host.Get<UserPasswordIssuer>().TakeIssued(target));
    }

    [Fact]
    public async Task A_new_password_cancelled_before_its_commit_keeps_the_authenticator_and_two_factor_sign_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, target) = await AdministratorAndTargetAsync(cancellationToken);
        await GiveSecondFactorsAsync(target, cancellationToken);
        using var cancellation = new CancellationTokenSource();
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), interceptors: [new CancelAtTokenRemoval(cancellation)]);
        var users = host.Get<UserService>();
        var updater = (await users.LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;

        Result? answer = null;
        try
        {
            answer = await users.UpdateApplicationUserAndTransitionAsync(updater, ApplicationUserWorkflowAttribute.NewPasswordStep, null, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(answer?.IsSuccess ?? false);
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.Equal(2, await check.UserTokens.CountAsync(token => token.UserId == target, cancellationToken));
        }

        Assert.True((await StoredAsync(target, cancellationToken)).TwoFactorEnabled);
    }

    [Fact]
    public async Task A_new_password_cancelled_after_its_save_commits_and_comes_back_to_be_shown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, target) = await AdministratorAndTargetAsync(cancellationToken);
        using var cancellation = new CancellationTokenSource();
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), interceptors: [new CancelAtPasswordSave(cancellation, afterTheSave: true)]);
        var users = host.Get<UserService>();
        var updater = (await users.LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;

        var answer = await users.UpdateApplicationUserAndTransitionAsync(updater, ApplicationUserWorkflowAttribute.NewPasswordStep, null, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(answer.IsSuccess, answer.Error);
        var shown = PasswordIn(answer.Message);
        var stored = await StoredAsync(target, cancellationToken);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<ApplicationUser>().VerifyHashedPassword(stored, stored.PasswordHash!, shown));
        Assert.Null(host.Get<UserPasswordIssuer>().TakeIssued(target));
    }

    private async Task<(TwoTenantScenario Scenario, Guid Target)> AdministratorAndTargetAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        return (scenario, await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Target", cancellationToken));
    }

    private async Task LockOutAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET LockoutEnd = {DateTimeOffset.UtcNow.AddMinutes(15)}, AccessFailedCount = 5, LockoutEnabled = 1 WHERE Id = {userId}", cancellationToken);
    }

    private async Task GiveSecondFactorsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        context.UserTokens.Add(new IdentityUserToken<Guid> { UserId = userId, LoginProvider = "[AspNetUserStore]", Name = "AuthenticatorKey", Value = "JBSWY3DPEHPK3PXP" });
        context.UserTokens.Add(new IdentityUserToken<Guid> { UserId = userId, LoginProvider = "[AspNetUserStore]", Name = "RecoveryCodes", Value = "AAAA-BBBB;CCCC-DDDD" });
        await context.SaveChangesAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET TwoFactorEnabled = 1 WHERE Id = {userId}", cancellationToken);
    }

    private async Task<ClaimsPrincipal> CookieOfAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
        return await host.Get<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
    }

    private static string PasswordIn(string? message)
    {
        Assert.NotNull(message);
        var start = message.IndexOf("password is ", StringComparison.Ordinal) + "password is ".Length;
        return message[start..message.IndexOf(' ', start)];
    }

    private async Task<ApplicationUser> StoredAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
    }

    private sealed class CancelAtTokenRemoval : SaveChangesInterceptor
    {
        private readonly CancellationTokenSource cancellation;

        public CancelAtTokenRemoval(CancellationTokenSource cancellation)
        {
            this.cancellation = cancellation;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<IdentityUserToken<Guid>>().Any(entry => entry.State == EntityState.Deleted) == true)
            {
                this.cancellation.Cancel();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CancelAtPasswordSave : SaveChangesInterceptor
    {
        private bool storesPassword;
        private readonly CancellationTokenSource cancellation;
        private readonly bool afterTheSave;

        public CancelAtPasswordSave(CancellationTokenSource cancellation, bool afterTheSave)
        {
            this.cancellation = cancellation;
            this.afterTheSave = afterTheSave;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            this.storesPassword = eventData.Context?.ChangeTracker.Entries<ApplicationUser>().Any(entry => entry.State == EntityState.Modified && entry.Property(user => user.PasswordHash).IsModified) == true;
            if (this.storesPassword && !this.afterTheSave)
            {
                this.cancellation.Cancel();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (this.storesPassword && this.afterTheSave)
            {
                this.cancellation.Cancel();
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}
