using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class SignInRefusalTests : IClassFixture<RealDatabase>
{
    private const string Password = "Password123!";

    private readonly RealDatabase database;

    public SignInRefusalTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task An_active_user_of_an_Active_tenant_signs_in_and_gets_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Company Active", cancellationToken);

        Assert.True(await SignsInAsync(tenant.OwnerId));
        Assert.Equal(tenant.OwnerId, (await SessionCircuit.ResolveAsync(this.database, tenant.OwnerId)).UserId);
    }

    [Theory]
    [InlineData(TenantState.Setup)]
    [InlineData(TenantState.Suspended)]
    [InlineData(TenantState.PendingDeletion)]
    [InlineData(TenantState.Archived)]
    public async Task A_user_of_a_tenant_that_is_not_Active_is_refused_at_sign_in_and_at_session_start(TenantState state)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, $"Company {state}", cancellationToken, state);

        Assert.False(await SignsInAsync(tenant.OwnerId));
        AssertNoSession(await SessionCircuit.ResolveAsync(this.database, tenant.OwnerId));
    }

    [Fact]
    public async Task A_deactivated_user_is_refused_at_sign_in_and_their_existing_cookie_opens_no_session_at_the_next_circuit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Company Deactivating", cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Member", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, tenant, tenant.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        Assert.True(await SignsInAsync(member));
        Assert.Equal(member, (await SessionCircuit.ResolveAsync(this.database, member)).UserId);

        await using (var host = ServiceHost.Create(this.database, tenant.Runtime()))
        {
            var saved = await host.Get<UserService>().StepUserAsync(member, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.True(saved.IsSuccess, saved.Error);
        }

        Assert.False(await SignsInAsync(member));
        AssertNoSession(await SessionCircuit.ResolveAsync(this.database, member));
    }

    [Fact]
    public async Task A_user_of_a_tenant_the_platform_owner_suspends_is_refused_at_sign_in_and_their_existing_cookie_opens_no_session_at_the_next_circuit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Company Suspending", cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        Assert.True(await SignsInAsync(tenant.OwnerId));
        Assert.Equal(tenant.OwnerId, (await SessionCircuit.ResolveAsync(this.database, tenant.OwnerId)).UserId);

        await using (var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId)))
        {
            var suspended = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenant.TenantId, "tenant-suspend", cancellationToken: cancellationToken);
            Assert.True(suspended.IsSuccess, suspended.Error);
        }

        Assert.False(await SignsInAsync(tenant.OwnerId));
        AssertNoSession(await SessionCircuit.ResolveAsync(this.database, tenant.OwnerId));

        Assert.Equal(platform.OwnerId, (await SessionCircuit.ResolveAsync(this.database, platform.OwnerId)).UserId);
    }

    [Fact]
    public async Task The_cookie_of_a_deactivated_user_or_of_a_user_whose_tenant_is_not_Active_fails_the_security_stamp_check()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Company Cookies", cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Member", cancellationToken);
        var suspended = await TwoTenantScenario.AddTenantAsync(this.database, "Company Suspended Cookies", cancellationToken, TenantState.Suspended);

        var ownerCookie = await CookieOfAsync(tenant.OwnerId);
        var memberCookie = await CookieOfAsync(member);
        var suspendedCookie = await CookieOfAsync(suspended.OwnerId);
        Assert.NotNull(await ValidateAsync(memberCookie));

        await using (var context = this.database.CreateSystemContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET State = {(int)UserState.Inactive} WHERE Id = {member}", cancellationToken);
        }

        Assert.Null(await ValidateAsync(memberCookie));
        Assert.Null(await ValidateAsync(suspendedCookie));
        Assert.NotNull(await ValidateAsync(ownerCookie));
    }

    [Fact]
    public async Task Deactivating_a_user_through_the_user_service_rotates_their_security_stamp_so_their_old_cookie_stays_refused_after_reactivation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Company Rotating", cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Member", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, tenant, tenant.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var cookie = await CookieOfAsync(member);

        string? before;
        await using (var context = this.database.CreateSystemContext())
        {
            before = (await context.Users.AsNoTracking().SingleAsync(row => row.Id == member, cancellationToken)).SecurityStamp;
        }

        await using (var host = ServiceHost.Create(this.database, tenant.Runtime()))
        {
            var users = host.Get<UserService>();
            Assert.True((await users.StepUserAsync(member, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);

            Assert.True((await users.StepUserAsync(member, ApplicationUserWorkflowAttribute.ReactivateStep)).IsSuccess);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            Assert.NotEqual(before, (await context.Users.AsNoTracking().SingleAsync(row => row.Id == member, cancellationToken)).SecurityStamp);
        }

        Assert.Null(await ValidateAsync(cookie));
        Assert.NotNull(await ValidateAsync(await CookieOfAsync(member)));
    }

    private async Task<ClaimsPrincipal> CookieOfAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
        return await host.Get<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
    }

    private async Task<ApplicationUser?> ValidateAsync(ClaimsPrincipal cookie)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        return await host.Get<SignInManager<ApplicationUser>>().ValidateSecurityStampAsync(cookie);
    }

    private async Task<bool> SignsInAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var users = host.Get<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId.ToString()))!;
        if (!await users.HasPasswordAsync(user))
        {
            Assert.True((await users.AddPasswordAsync(user, Password)).Succeeded);
        }

        var result = await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(user, Password, lockoutOnFailure: false);
        Assert.False(result.IsLockedOut);
        return result.Succeeded;
    }

    private static void AssertNoSession(RuntimeExecutionContext runtime) =>
        Assert.Equal((false, (Guid?)null, (Guid?)null), (runtime.IsInitialized, runtime.UserId, runtime.TenantId));
}
