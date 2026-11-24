using Staffora.Services.Platform;
using Staffora.Core.Lists;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Blazor.Components.Account;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class SystemAccountAccessTests : IClassFixture<RealDatabase>
{
    private const string Password = "Password123!";
    private const string SystemAccountNameRefused = "A user name starting with \"system:\" is reserved for a tenant's system account.";

    private readonly RealDatabase database;

    public SystemAccountAccessTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_system_account_is_refused_at_sign_in_even_with_a_password_while_an_ordinary_user_signs_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var signIn = host.Get<SignInManager<ApplicationUser>>();
            Assert.IsType<ApplicationSignInManager>(signIn);
            var account = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(scenario.A.SystemAccountId.ToString()))!;
            Assert.Null(account.PasswordHash);
            Assert.True((await signIn.PasswordSignInAsync(account.UserName!, Password, isPersistent: false, lockoutOnFailure: false)).IsNotAllowed);
        }

        await SetPasswordAsync(scenario.A.SystemAccountId, cancellationToken);
        await SetPasswordAsync(scenario.A.OwnerId, cancellationToken);
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var signIn = host.Get<SignInManager<ApplicationUser>>();
            var users = host.Get<UserManager<ApplicationUser>>();
            var account = (await users.FindByIdAsync(scenario.A.SystemAccountId.ToString()))!;
            var owner = (await users.FindByIdAsync(scenario.A.OwnerId.ToString()))!;

            Assert.True(await users.CheckPasswordAsync(account, Password));
            Assert.False(await signIn.CanSignInAsync(account));
            Assert.True((await signIn.CheckPasswordSignInAsync(account, Password, lockoutOnFailure: false)).IsNotAllowed);
            Assert.True((await signIn.PasswordSignInAsync(account.UserName!, Password, isPersistent: false, lockoutOnFailure: false)).IsNotAllowed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.SignInAsync(account, isPersistent: false));

            Assert.True(await signIn.CanSignInAsync(owner));
            Assert.True((await signIn.CheckPasswordSignInAsync(owner, Password, lockoutOnFailure: false)).Succeeded);
        }
    }

    [Fact]
    public async Task The_user_lists_never_show_a_system_account_and_reading_one_by_id_finds_no_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);

        await using (var tenantHost = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var users = tenantHost.Get<UserService>();
            Assert.Equal([scenario.A.OwnerId], (await users.QueryUsersAsync(new ListQuery())).Value!.Rows.Select(user => user.Id));
            Assert.Equal("User not found.", (await users.GetUserByIdAsync(scenario.A.SystemAccountId)).Error);
            Assert.Equal("User not found.", (await users.StepUserAsync(scenario.A.SystemAccountId, ApplicationUserWorkflowAttribute.NewPasswordStep)).Error);
        }

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var users = console.Get<UserService>();
            var platformConsole = console.Get<PlatformConsoleService>();
            foreach (var tenant in new[] { scenario.A, scenario.B, platform })
            {
                var listed = (await platformConsole.QueryTenantUsersAsync(tenant.TenantId, new ListQuery())).Value!.Rows.Select(user => user.Id).ToList();
                Assert.Contains(tenant.OwnerId, listed);
                Assert.DoesNotContain(tenant.SystemAccountId, listed);
            }

            Assert.Equal([scenario.B.OwnerId], (await platformConsole.QueryTenantUsersAsync(scenario.B.TenantId, new ListQuery())).Value!.Rows.Select(user => user.Id));
            Assert.False((await users.GetUserByIdAsync(scenario.B.SystemAccountId)).IsSuccess);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(3, await check.Users.CountAsync(user => user.IsSystemAccount && (user.Id == scenario.A.SystemAccountId || user.Id == scenario.B.SystemAccountId || user.Id == platform.SystemAccountId), cancellationToken));
    }

    [Fact]
    public async Task A_user_created_with_a_system_accounts_id_and_flag_becomes_an_ordinary_user_with_a_new_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = new Tenant { Name = "Company C", State = TenantState.Active };
        var founder = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"founder.{Guid.NewGuid():N}", FirstName = "Founder", LastName = "C", State = UserState.Active };
        await using (var context = this.database.CreateSystemContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync(cancellationToken);
            founder.NormalizedUserName = founder.UserName!.ToUpperInvariant();
            founder.TenantId = tenant.Id;
            founder.CreatedById = founder.Id;
            context.Users.Add(founder);
            await context.SaveChangesAsync(cancellationToken);
        }

        var companyC = new ScenarioTenant(tenant.Id, tenant.Name, founder.Id);
        await TwoTenantScenario.GrantAsync(this.database, companyC, founder.Id, cancellationToken, RolePermission.UsersCreate);

        var accountId = SystemAccount.IdFor(tenant.Id);
        await using (var console = ServiceHost.Create(this.database, companyC.Runtime(), services => services.Configure<IdentityOptions>(options => options.User.AllowedUserNameCharacters += ":")))
        {
            var users = console.Get<UserService>();
            var crafted = new ApplicationUser { Id = accountId, IsSystemAccount = true, UserName = "crafted.user@company-c.example", Email = "crafted.user@company-c.example", FirstName = "Crafted", LastName = "User" };
            var created = await users.CreateUserAsync(crafted, tenant.Id, Password);
            Assert.True(created.IsSuccess, created.Error);
            Assert.NotEqual(accountId, created.Value!.Id);
            Assert.Equal(4, created.Value.Id.Version);
            Assert.False(created.Value.IsSystemAccount);

            var named = await users.CreateUserAsync(new ApplicationUser { UserName = SystemAccount.UserNameFor(tenant.Id), Email = "named@company-c.example" }, tenant.Id, Password);
            Assert.Equal(SystemAccountNameRefused, named.Error);
        }

        await using (var check = this.database.CreateSystemContext())
        {
            var stored = await check.Users.AsNoTracking().SingleAsync(user => user.Email == "crafted.user@company-c.example", cancellationToken);
            Assert.False(stored.IsSystemAccount);
            Assert.Equal(4, stored.Id.Version);
            Assert.False(await check.Users.AnyAsync(user => user.Id == accountId, cancellationToken));
            Assert.False(await check.Users.AnyAsync(user => user.Email == "named@company-c.example", cancellationToken));
        }

        await using var host = ServiceHost.Create(this.database, companyC.Runtime());
        Assert.True((await host.Get<SystemAccountService>().EnsureAsync(tenant.Id, cancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Renaming_a_user_to_a_system_accounts_name_is_refused_and_a_rename_updates_the_normalized_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var userId = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Renamed", cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        var originalName = (await users.GetUserByIdAsync(userId)).Value!.UserName;

        Assert.False((await users.SaveUserAsync(userId, user => user.UserName = SystemAccount.UserNameFor(scenario.A.TenantId))).IsSuccess);

        await using (var colonAllowed = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.Configure<IdentityOptions>(options => options.User.AllowedUserNameCharacters += ":")))
        {
            var lenient = colonAllowed.Get<UserService>();
            Assert.Equal(SystemAccountNameRefused, (await lenient.SaveUserAsync(userId, again => again.UserName = SystemAccount.UserNameFor(Guid.NewGuid()))).Error);
        }

        var renamed = await users.SaveUserAsync(userId, user => user.UserName = "renamed.user@company-a.example");
        Assert.True(renamed.IsSuccess, renamed.Error);

        await using var check = this.database.CreateSystemContext();
        var stored = await check.Users.AsNoTracking().SingleAsync(row => row.Id == userId, cancellationToken);
        Assert.NotEqual(originalName, stored.UserName);
        Assert.Equal(("renamed.user@company-a.example", "RENAMED.USER@COMPANY-A.EXAMPLE"), (stored.UserName, stored.NormalizedUserName));
        Assert.Equal(userId, (await host.Get<UserManager<ApplicationUser>>().FindByNameAsync("renamed.user@company-a.example"))?.Id);
    }

    [Fact]
    public async Task A_role_is_refused_to_a_system_account_and_to_a_user_of_another_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.RolesEdit);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        Guid roleId;
        Guid unitId;
        await using (var context = this.database.CreateSystemContext())
        {
            var holding = await context.OrganizationUnitUserRoles.AsNoTracking().SingleAsync(row => row.UserId == scenario.A.OwnerId, cancellationToken);
            (roleId, unitId) = (holding.TenantRoleId, holding.OrganizationUnitId);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var tenants = host.Get<RoleHoldingService>();
            Assert.Equal("A tenant's system account holds no role.", (await tenants.AssignUserToRoleAsync(scenario.A.SystemAccountId, unitId, roleId, scenario.A.TenantId)).Error);
            Assert.Equal("The user is not a user of this tenant.", (await tenants.AssignUserToRoleAsync(scenario.B.OwnerId, unitId, roleId, scenario.A.TenantId)).Error);
            Assert.True((await tenants.AssignUserToRoleAsync(colleague, unitId, roleId, scenario.A.TenantId)).IsSuccess);
        }

        await using var check = this.database.CreateSystemContext();
        var holders = await check.OrganizationUnitUserRoles.Where(row => row.TenantRoleId == roleId).Select(row => row.UserId).ToListAsync(cancellationToken);
        Assert.Equal(new[] { colleague, scenario.A.OwnerId }.Order(), holders.Order());
    }

    [Fact]
    public async Task A_cookie_naming_a_system_account_fails_revalidation_and_opens_no_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        ClaimsPrincipal accountCookie;
        ClaimsPrincipal ownerCookie;

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var users = host.Get<UserManager<ApplicationUser>>();
            var claims = host.Get<IUserClaimsPrincipalFactory<ApplicationUser>>();
            accountCookie = await claims.CreateAsync((await users.FindByIdAsync(scenario.A.SystemAccountId.ToString()))!);
            ownerCookie = await claims.CreateAsync((await users.FindByIdAsync(scenario.A.OwnerId.ToString()))!);

            var signIn = host.Get<SignInManager<ApplicationUser>>();
            Assert.Null(await signIn.ValidateSecurityStampAsync(accountCookie));
            Assert.NotNull(await signIn.ValidateSecurityStampAsync(ownerCookie));

            using var circuit = new IdentityRevalidatingAuthenticationStateProvider(NullLoggerFactory.Instance, host.Get<IServiceScopeFactory>(), host.Get<IServiceProvider>());
            Assert.False(await circuit.IsStillValidAsync(accountCookie));
            Assert.True(await circuit.IsStillValidAsync(ownerCookie));
        }

        var (accountHost, accountRuntime) = SessionCircuit.Open(this.database, accountCookie);
        await using (accountHost)
        {
            var session = accountHost.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();
            Assert.False(accountRuntime.IsInitialized);
            Assert.Equal(Guid.Empty, session.UserId);
        }

        var (ownerHost, ownerRuntime) = SessionCircuit.Open(this.database, ownerCookie);
        await using (ownerHost)
        {
            await ownerHost.Get<UserSessionService>().InitializeFromClaimsAsync();
            Assert.True(ownerRuntime.IsInitialized);
            Assert.Equal(scenario.A.OwnerId, ownerRuntime.UserId);
        }
    }

    [Fact]
    public async Task Impersonating_a_system_account_is_refused_when_started_and_when_a_cookie_names_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var ownerCookie = SessionCircuit.Cookie(platform.OwnerId, AppRoles.Admin);

        ClaimsPrincipal impersonatingOwner;
        await using (var endpoint = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var impersonation = endpoint.Get<ImpersonationService>();
            Assert.Equal(ImpersonationAuthorizer.SystemAccountRefused, (await impersonation.StartAsync(ownerCookie, scenario.A.SystemAccountId, cancellationToken)).Error);

            var started = await impersonation.StartAsync(ownerCookie, scenario.A.OwnerId, cancellationToken);
            Assert.True(started.IsSuccess, started.Error);
            impersonatingOwner = started.Value!;
        }

        var sessionId = ImpersonationClaims.Read(impersonatingOwner)!.SessionId;
        var namingTheAccount = ImpersonationClaims.Reissue(ownerCookie, [], new ImpersonationClaim(scenario.A.SystemAccountId, sessionId));
        var refused = await SessionCircuit.ResolveAsync(this.database, namingTheAccount);
        Assert.Equal((false, (Guid?)null), (refused.IsInitialized, refused.EffectiveUserId));
        Assert.Equal(scenario.A.OwnerId, (await SessionCircuit.ResolveAsync(this.database, impersonatingOwner)).EffectiveUserId);
    }

    private async Task SetPasswordAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.SingleAsync(row => row.Id == userId, cancellationToken);
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, Password);
        context.NameEditor(user, SystemAccount.IdFor(user.TenantId));
        await context.SaveChangesAsync(cancellationToken);
    }
}
