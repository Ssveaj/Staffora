using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed partial class TenantOnboardingTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantOnboardingTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tenant_onboarded_through_the_service_signs_in_its_owner_who_creates_a_coordinator_with_no_step_done_by_hand()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct);
        Assert.Equal(TenantState.Setup, await TenantStateAsync(onboarded.TenantId));

        Assert.True(await SignsInAsync(onboarded.OwnerEmail, onboarded.OwnerPassword));
        var session = await SessionCircuit.ResolveAsync(this.database, onboarded.OwnerId);
        Assert.Equal((onboarded.OwnerId, (Guid?)onboarded.TenantId), (session.UserId, session.TenantId));

        Guid coordinatorId;
        string coordinatorEmail = $"coordinator.{Guid.NewGuid():N}@northwind.example";
        string coordinatorPassword;
        await using (var host = ServiceHost.Create(this.database, session))
        {
            var created = await host.Get<UserService>().CreateApplicationUserAsync(new ApplicationUser { FirstName = "Casey", LastName = "Lind", Email = coordinatorEmail, UserName = coordinatorEmail }, ApplicationUserWorkflowAttribute.CreateStep, Ct);
            Assert.True(created.IsSuccess, created.Error);
            coordinatorId = created.Value;
            coordinatorPassword = InitialPassword().Match(created.Message!).Groups["password"].Value;

            var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, onboarded.Tenant, Ct);
            var appointed = await host.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinatorId, rootId, onboarded.Roles.Coordinator, onboarded.TenantId);
            Assert.True(appointed.IsSuccess, appointed.Error);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: read the coordinator");
            var coordinator = await system.Users.AsNoTracking().SingleAsync(user => user.Id == coordinatorId, Ct);
            Assert.Equal((UserState.Active, onboarded.TenantId, onboarded.OwnerId), (coordinator.State, coordinator.TenantId, coordinator.CreatedById));
            Assert.True(await system.OrganizationUnitUserRoles.AnyAsync(holding => holding.UserId == coordinatorId && holding.TenantRoleId == onboarded.Roles.Coordinator, Ct));
        }

        Assert.False(await SignsInAsync(coordinatorEmail, coordinatorPassword));

        await ActivateAsync(onboarded);
        Assert.True(await SignsInAsync(coordinatorEmail, coordinatorPassword));
    }

    [Fact]
    public async Task Another_user_of_a_tenant_in_Setup_is_refused_at_sign_in_the_session_gate_the_cookie_check_and_impersonation_while_its_owner_is_admitted()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct);
        var other = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Member", Ct);
        await TwoTenantScenario.GrantAsync(this.database, onboarded.Tenant, other, Ct, [.. RolePresets.Administrator.Permissions]);
        const string password = "Member-password-7";
        await SetPasswordAsync(other, password);

        await using (var host = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var signIn = (ApplicationSignInManager)host.Get<SignInManager<ApplicationUser>>();
            var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(other.ToString()))!;
            Assert.True((await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false)).IsNotAllowed);
            Assert.Equal(SignInRefusal.TenantNotActive, signIn.Refusal);
        }

        var otherSession = await SessionCircuit.ResolveAsync(this.database, other);
        Assert.Equal((false, (Guid?)null), (otherSession.IsInitialized, otherSession.UserId));
        Assert.Equal(onboarded.OwnerId, (await SessionCircuit.ResolveAsync(this.database, onboarded.OwnerId)).UserId);
        Assert.False(await CookieStandsAsync(other));
        Assert.True(await CookieStandsAsync(onboarded.OwnerId));

        await using (var host = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime()))
        {
            var authorizer = host.Get<ImpersonationAuthorizer>();
            Assert.True((await authorizer.AuthorizeAsync(onboarded.Platform.OwnerId, onboarded.OwnerId, Ct)).IsSuccess);
            Assert.Equal(ImpersonationAuthorizer.TenantNotActive, (await authorizer.AuthorizeAsync(onboarded.Platform.OwnerId, other, Ct)).Error);
            var listed = (await authorizer.ListEligibleTargetsAsync(onboarded.Platform.OwnerId, Ct)).Value!.Select(candidate => candidate.UserId).ToList();
            Assert.Contains(onboarded.OwnerId, listed);
            Assert.DoesNotContain(other, listed);
        }

        await UserEdits.SetStateInTableAsync(this.database, onboarded.OwnerId, UserState.Inactive, Ct);
        Assert.False(await SignsInAsync(onboarded.OwnerEmail, onboarded.OwnerPassword));
    }

    [Fact]
    public async Task The_new_tenants_system_account_is_in_place_so_a_platform_owners_Activate_saves_its_history_and_Activate_needs_an_active_owner()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct);
        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: read the new tenant's system account");
            Assert.True(await system.Users.AnyAsync(user => user.Id == SystemAccount.IdFor(onboarded.TenantId) && user.IsSystemAccount && user.TenantId == onboarded.TenantId, Ct));
        }

        await ActivateAsync(onboarded);
        Assert.Equal(TenantState.Active, await TenantStateAsync(onboarded.TenantId));
        var history = await HistoryOfAsync(onboarded.TenantId);
        Assert.Equal(
            [(TenantWorkflowAttribute.SaveSetupStep, TenantState.Setup, TenantState.Setup), (TenantWorkflowAttribute.ActivateStep, TenantState.Setup, TenantState.Active)],
            history.Select(entry => (entry.TransitionKey, (TenantState)entry.FromState, (TenantState)entry.ToState)));
        Assert.All(history, entry => Assert.Equal((onboarded.Platform.OwnerId, SystemAccount.IdFor(onboarded.TenantId), onboarded.TenantId), (entry.ActorId, entry.CreatedById, entry.TenantId)));

        var bare = await TwoTenantScenario.AddTenantAsync(this.database, "Company Ownerless", Ct, TenantState.Setup);
        await using var host = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime());
        var refused = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(bare.TenantId, TenantWorkflowAttribute.ActivateStep, TenantWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Equal(TenantWorkflowAttribute.ActiveOwnerReason, refused.Error);
        Assert.Equal(TenantState.Setup, await TenantStateAsync(bare.TenantId));
        var answers = await host.Get<WorkflowService>().GetAvailableTransitionsAsync<TenantState, Tenant>(bare.TenantId, TenantWorkflowQueries.WithWorkflowGraph, Ct);
        Assert.Contains(TenantWorkflowAttribute.ActiveOwnerReason, answers.Value!.Single(answer => answer.Transition.Key == TenantWorkflowAttribute.ActivateStep).BlockingReasons);
    }

    [Fact]
    public async Task Onboarding_writes_the_presets_and_the_first_owner_on_the_tenants_behalf_with_their_histories()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct);
        var account = SystemAccount.IdFor(onboarded.TenantId);
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read what onboarding wrote");

        var roles = await system.TenantRoles.AsNoTracking().Include(role => role.Permissions).Where(role => role.TenantId == onboarded.TenantId).ToListAsync(Ct);
        Assert.Equal(
            new[] { RolePresets.SystemOwner, RolePresets.Administrator, RolePresets.Coordinator, RolePresets.Staff }.Select(preset => (preset.Name, preset.Description, Permissions: string.Join(",", preset.Permissions.Order()))).OrderBy(row => row.Name),
            roles.Select(role => (role.Name, role.Description!, Permissions: string.Join(",", role.Permissions.Select(row => row.Permission).Order()))).OrderBy(row => row.Name));
        Assert.Equal([onboarded.Roles.SystemOwner], roles.Where(role => role.IsSystemOwner).Select(role => role.Id));
        Assert.All(roles, role => Assert.Equal(TenantRoleState.Active, role.State));
        Assert.Equal(RolePermissions.Grantable.Where(permission => !RolePermissions.IsPersonal(permission) && !RolePermissions.IsPortal(permission)).Order(), RolePresets.SystemOwner.Permissions.Order());
        Assert.Equal(RolePermissions.Personal.Order(), RolePresets.Staff.Permissions.Order());

        var owner = await system.Users.AsNoTracking().SingleAsync(user => user.Id == onboarded.OwnerId, Ct);
        Assert.Equal((onboarded.TenantId, UserState.Active, false, onboarded.OwnerEmail, onboarded.OwnerEmail.ToUpperInvariant()), (owner.TenantId, owner.State, owner.IsSystemAccount, owner.UserName, owner.NormalizedEmail));
        Assert.NotNull(owner.PasswordHash);
        var holding = await system.OrganizationUnitUserRoles.AsNoTracking().Include(row => row.OrganizationUnit).SingleAsync(row => row.UserId == onboarded.OwnerId, Ct);
        Assert.Equal((onboarded.Roles.SystemOwner, OrganizationUnitType.Company), (holding.TenantRoleId, holding.OrganizationUnit.UnitType));

        var permissions = await system.TenantRolePermissions.AsNoTracking().Where(row => row.TenantId == onboarded.TenantId).ToListAsync(Ct);
        Assert.All(roles, role => Assert.Equal((account, (Guid?)null), (role.CreatedById, role.ImpersonatedById)));
        Assert.All(permissions, row => Assert.Equal(account, row.CreatedById));
        Assert.Equal((account, (Guid?)null), (owner.CreatedById, owner.ImpersonatedById));
        Assert.Equal(account, holding.CreatedById);

        var entries = await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.TenantId == onboarded.TenantId && entry.EntityId != onboarded.TenantId).ToListAsync(Ct);
        Assert.Equal(
            new[] { (onboarded.OwnerId, ApplicationUserWorkflowAttribute.CreateStep) }.Concat(roles.Select(role => (role.Id, TenantRoleWorkflowAttribute.CreateStep))).Order(),
            entries.Select(entry => (entry.EntityId, entry.TransitionKey)).Order());
        Assert.All(entries, entry => Assert.Equal((0, 1, onboarded.Platform.OwnerId, account), (entry.FromState, entry.ToState, entry.ActorId, entry.CreatedById)));
    }

    [Fact]
    public async Task A_refused_onboarding_and_one_that_fails_inside_store_nothing()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var existing = await TwoTenantScenario.AddTenantAsync(this.database, "Company Existing", Ct);
        string heldAddress;
        await using (var context = this.database.CreateSystemContext())
        {
            heldAddress = (await context.Users.AsNoTracking().SingleAsync(user => user.Id == existing.OwnerId, Ct)).Email!;
        }

        var before = await CountsAsync();
        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var onboarding = host.Get<TenantOnboardingService>();
            var name = TwoTenantScenario.UniqueName("Company Refused");
            Assert.Equal(TenantOnboardingService.OwnerRequired, (await onboarding.OnboardTenantAsync(new TenantOnboarding(name, null, "Robin", " ", "robin@refused.example"), Ct)).Error);
            Assert.Equal(TenantOnboardingService.OwnerNameTooLong, (await onboarding.OnboardTenantAsync(new TenantOnboarding(name, null, new string('R', 101), "Hale", "robin@refused.example"), Ct)).Error);

            Assert.Equal(UserService.SignInNameUnavailable, (await onboarding.OnboardTenantAsync(new TenantOnboarding(name, null, "Robin", "Hale", heldAddress.ToUpperInvariant()), Ct)).Error);
            Assert.Equal(TenantService.NameTaken, (await onboarding.OnboardTenantAsync(new TenantOnboarding(existing.Name, null, "Robin", "Hale", $"robin.{Guid.NewGuid():N}@refused.example"), Ct)).Error);
            Assert.Contains("Name", (await onboarding.OnboardTenantAsync(new TenantOnboarding("  ", null, "Robin", "Hale", $"robin.{Guid.NewGuid():N}@refused.example"), Ct)).Error, StringComparison.Ordinal);
            Assert.Equal(SystemAccountNameRefusedText, (await onboarding.OnboardTenantAsync(new TenantOnboarding(name, null, "Robin", "Hale", "system:owner"), Ct)).Error);
        }

        Assert.Equal(before, await CountsAsync());

        var failing = new FailsTheHoldingsSave();
        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), interceptors: [failing]))
        {
            var failed = await host.Get<TenantOnboardingService>().OnboardTenantAsync(new TenantOnboarding(TwoTenantScenario.UniqueName("Company Failing"), null, "Robin", "Hale", $"robin.{Guid.NewGuid():N}@failing.example"), Ct);
            Assert.False(failed.IsSuccess);
        }

        Assert.True(failing.Failed, "the holding's save was reached and failed");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task An_onboarding_with_a_text_longer_than_its_column_is_refused_with_what_is_wrong_and_stores_nothing()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var before = await CountsAsync();
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var onboarding = host.Get<TenantOnboardingService>();
        async Task<string?> Refusal(TenantOnboarding request) => (await onboarding.OnboardTenantAsync(request, Ct)).Error;
        var name = TwoTenantScenario.UniqueName("Company Overlong");
        static string Address() => $"robin.{Guid.NewGuid():N}@overlong.example";

        Assert.Equal(TenantOnboardingService.OwnerEmailTooLong, await Refusal(new TenantOnboarding(name, null, "Robin", "Hale", new string('a', 250) + "@x.example")));
        Assert.Equal(TenantOnboardingService.TenantDescriptionTooLong, await Refusal(new TenantOnboarding(name, new string('d', 2000), "Robin", "Hale", Address())));
        Assert.Equal(TenantOnboardingService.TenantNameTooLong, await Refusal(new TenantOnboarding(new string('n', 400), null, "Robin", "Hale", Address())));
        Assert.Equal(TenantOnboardingService.OwnerNameTooLong, await Refusal(new TenantOnboarding(name, null, "Robin", new string('H', 101), Address())));
        Assert.Equal(before, await CountsAsync());

        var atLimit = await onboarding.OnboardTenantAsync(new TenantOnboarding(" " + name.PadRight(TenantOnboardingService.TenantNameMaxLength, 'x') + " ", new string('d', TenantOnboardingService.TenantDescriptionMaxLength), new string('R', TenantOnboardingService.NameMaxLength), "Hale", Address()), Ct);
        Assert.True(atLimit.IsSuccess, atLimit.Error);

        await using var context = this.database.CreateSystemContext();
        int? MaxLength<T>(string property) => context.Model.FindEntityType(typeof(T))!.FindProperty(property)!.GetMaxLength();
        Assert.Equal(
            (TenantOnboardingService.TenantNameMaxLength, TenantOnboardingService.TenantDescriptionMaxLength, TenantOnboardingService.NameMaxLength, TenantOnboardingService.NameMaxLength, TenantOnboardingService.EmailMaxLength, TenantOnboardingService.EmailMaxLength),
            (MaxLength<Tenant>(nameof(Tenant.Name)), MaxLength<Tenant>(nameof(Tenant.Description)), MaxLength<ApplicationUser>(nameof(ApplicationUser.FirstName)), MaxLength<ApplicationUser>(nameof(ApplicationUser.LastName)), MaxLength<ApplicationUser>(nameof(ApplicationUser.Email)), MaxLength<ApplicationUser>(nameof(ApplicationUser.UserName))));
    }

    [Fact]
    public async Task The_onboarded_answer_never_prints_the_initial_password()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct);
        var answer = new OnboardedTenant(onboarded.TenantId, onboarded.OwnerId, onboarded.OwnerEmail, onboarded.OwnerPassword);

        var printed = answer.ToString();
        Assert.DoesNotContain(onboarded.OwnerPassword, printed, StringComparison.Ordinal);
        Assert.DoesNotContain(onboarded.OwnerPassword, $"{answer}", StringComparison.Ordinal);
        Assert.Contains(onboarded.TenantId.ToString(), printed, StringComparison.Ordinal);
        Assert.Contains(onboarded.OwnerEmail, printed, StringComparison.Ordinal);
        Assert.Contains("InitialPassword = (not shown)", printed, StringComparison.Ordinal);

        Assert.Equal(onboarded.OwnerPassword, answer.InitialPassword);
        Assert.Equal(answer, answer with { });
    }

    [Fact]
    public async Task Two_onboardings_with_one_owner_address_at_once_store_one_and_refuse_the_other_in_the_one_answer()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        for (var round = 0; round < 3; round++)
        {
            var email = $"twin.{Guid.NewGuid():N}@twins.example";
            await using var first = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
            await using var second = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
            var results = await Task.WhenAll(
                Task.Run(() => first.Get<TenantOnboardingService>().OnboardTenantAsync(new TenantOnboarding(TwoTenantScenario.UniqueName("Twin A"), null, "Robin", "Hale", email), Ct), Ct),
                Task.Run(() => second.Get<TenantOnboardingService>().OnboardTenantAsync(new TenantOnboarding(TwoTenantScenario.UniqueName("Twin B"), null, "Robin", "Hale", email), Ct), Ct));

            var stored = Assert.Single(results, result => result.IsSuccess);
            Assert.Equal(UserService.SignInNameUnavailable, Assert.Single(results, result => !result.IsSuccess).Error);
            await using var system = this.database.CreateSystemContext();
            using var lease = system.BeginSystemScope("test: count the twins");
            Assert.Equal([stored.Value!.OwnerId], await system.Users.Where(user => user.NormalizedEmail == email.ToUpperInvariant()).Select(user => user.Id).ToListAsync(Ct));
            Assert.Equal(1, await system.Tenants.CountAsync(tenant => tenant.Id == stored.Value.TenantId, Ct));
        }
    }

    [Fact]
    public async Task Only_a_platform_owner_on_the_console_onboards_a_tenant()
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, Ct, [.. RolePermissions.Grantable]);
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        var before = await CountsAsync();

        string heldAddress;
        await using (var context = this.database.CreateSystemContext())
        {
            heldAddress = (await context.Users.AsNoTracking().SingleAsync(user => user.Id == scenario.B.OwnerId, Ct)).Email!;
        }

        foreach (var runtime in new IRuntimeExecutionContext[] { scenario.A.Runtime(), impersonating })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            var onboarding = host.Get<TenantOnboardingService>();
            var refused = await onboarding.OnboardTenantAsync(new TenantOnboarding(TwoTenantScenario.UniqueName("Company Inside"), null, "Robin", "Hale", $"robin.{Guid.NewGuid():N}@inside.example"), Ct);
            Assert.Equal(TenantOnboardingService.ConsoleOnly, refused.Error);

            Assert.Equal(TenantOnboardingService.ConsoleOnly, (await onboarding.OnboardTenantAsync(new TenantOnboarding(TwoTenantScenario.UniqueName("Company Inside"), null, "Robin", "Hale", heldAddress), Ct)).Error);
            Assert.Equal(TenantOnboardingService.ConsoleOnly, (await onboarding.OnboardTenantAsync(new TenantOnboarding(TwoTenantScenario.UniqueName("Company Inside"), null, " ", null, null), Ct)).Error);
        }

        Assert.Equal(before, await CountsAsync());
    }

    private const string SystemAccountNameRefusedText = "A user name starting with \"" + SystemAccount.UserNamePrefix + "\" is reserved for a tenant's system account.";

    [GeneratedRegex("initial password is (?<password>\\S+) —")]
    private static partial Regex InitialPassword();

    private async Task ActivateAsync(OnboardedScenario onboarded)
    {
        await using var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, onboarded.Platform.OwnerId));
        var activated = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(onboarded.TenantId, TenantWorkflowAttribute.ActivateStep, TenantWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.True(activated.IsSuccess, activated.Error);
    }

    private async Task<bool> SignsInAsync(string email, string password)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = await host.Get<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        Assert.NotNull(user);
        var result = await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(user, password, lockoutOnFailure: false);
        Assert.False(result.IsLockedOut);
        return result.Succeeded;
    }

    private async Task SetPasswordAsync(Guid userId, string password)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var users = host.Get<UserManager<ApplicationUser>>();
        Assert.True((await users.AddPasswordAsync((await users.FindByIdAsync(userId.ToString()))!, password)).Succeeded);
    }

    private async Task<bool> CookieStandsAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
        var cookie = await host.Get<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
        return await ((ApplicationSignInManager)host.Get<SignInManager<ApplicationUser>>()).AdmitsCookieAsync(cookie, Ct);
    }

    private async Task<TenantState> TenantStateAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a tenant's state");
        return await system.Tenants.Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.State).SingleAsync(Ct);
    }

    private async Task<List<Staffora.Core.Entities.Workflow.StateHistoryEntry>> HistoryOfAsync(Guid entityId)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a history");
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == entityId).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(Ct);
    }

    private async Task<(int Tenants, int Users, int Units, int Settings, int Roles, int Holdings, int History)> CountsAsync()
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: count what onboarding writes");
        return (await system.Tenants.CountAsync(Ct), await system.Users.CountAsync(Ct), await system.OrganizationUnits.CountAsync(Ct), await system.TenantSettings.CountAsync(Ct),
            await system.TenantRoles.CountAsync(Ct), await system.OrganizationUnitUserRoles.CountAsync(Ct), await system.StateHistoryEntries.CountAsync(Ct));
    }

    private sealed class FailsTheHoldingsSave : SaveChangesInterceptor
    {
        public bool Failed { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && context.ChangeTracker.Entries<OrganizationUnitUserRole>().Any(entry => entry.State == EntityState.Added))
            {
                Failed = true;
                throw new InvalidOperationException("The holding's save fails.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
