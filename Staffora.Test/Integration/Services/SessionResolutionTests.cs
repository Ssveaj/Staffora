using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Blazor.Components.Account;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class SessionResolutionTests : IClassFixture<RealDatabase>
{
    private static readonly DateTimeOffset morning = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public SessionResolutionTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Concurrent_initializers_share_one_resolution_and_query_the_database_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView);
        var cookie = await CookieOfAsync(scenario.A.OwnerId);

        int single;
        var (one, _) = SessionCircuit.Open(this.database, cookie);
        await using (one)
        {
            await one.Get<UserSessionService>().InitializeFromClaimsAsync();
            single = one.CommandLog.Count;
        }

        var claims = new HeldAuthenticationState(cookie);
        var (many, runtime) = SessionCircuit.Open(this.database, cookie, services => services.AddSingleton<AuthenticationStateProvider>(claims));
        await using (many)
        {
            var session = many.Get<UserSessionService>();
            var entered = 0;
            var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                Interlocked.Increment(ref entered);
                return session.InitializeFromClaimsAsync();
            }, cancellationToken)).ToArray();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref entered) == callers.Length, TimeSpan.FromSeconds(10)));
            Assert.False(session.IsReady);

            claims.Release();
            await Task.WhenAll(callers);

            Assert.Equal(1, claims.Calls);
            Assert.Equal(single, many.CommandLog.Count);
            Assert.True(session.IsReady);
            Assert.Equal((scenario.A.OwnerId, scenario.A.TenantId), (runtime.UserId, runtime.TenantId));

            await session.InitializeFromClaimsAsync();
            Assert.Equal(single, many.CommandLog.Count);
        }
    }

    [Fact]
    public async Task The_permission_set_is_primed_before_the_session_is_ready()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView);

        var (host, _) = SessionCircuit.Open(this.database, await CookieOfAsync(scenario.A.OwnerId));
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            var permissions = host.Get<PermissionService>();
            await session.InitializeFromClaimsAsync();
            host.ClearCommandLog();

            Assert.True(session.IsReady);
            Assert.True(permissions.IsLoaded);
            Assert.True(await permissions.HasPermissionAsync(RolePermission.UsersView, cancellationToken));
            Assert.Empty(host.CommandLog);
        }
    }

    [Fact]
    public async Task A_role_removed_mid_session_is_gone_within_the_time_to_live()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView);
        var clock = new FakeClock(morning);

        var (host, _) = SessionCircuit.Open(this.database, await CookieOfAsync(scenario.A.OwnerId), services => services.AddSingleton<TimeProvider>(clock));
        await using (host)
        {
            var permissions = host.Get<PermissionService>();
            await host.Get<UserSessionService>().InitializeFromClaimsAsync();
            Assert.True(await permissions.HasPermissionAsync(RolePermission.UsersView, cancellationToken));

            await using (var context = this.database.CreateSystemContext())
            {
                context.OrganizationUnitUserRoles.RemoveRange(await context.OrganizationUnitUserRoles.Where(holding => holding.UserId == scenario.A.OwnerId).ToListAsync(cancellationToken));
                await context.SaveChangesAsync(cancellationToken);
            }

            clock.Advance(PermissionService.TimeToLive - TimeSpan.FromSeconds(1));
            Assert.True(await permissions.HasPermissionAsync(RolePermission.UsersView, cancellationToken));

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(permissions.IsLoaded);
            Assert.False(await permissions.HasPermissionAsync(RolePermission.UsersView, cancellationToken));
        }

        Assert.Equal(TimeSpan.FromMinutes(5), PermissionService.TimeToLive);
    }

    [Fact]
    public async Task Removing_ones_own_role_drops_the_cached_permissions_at_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.RolesEdit);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView);
        var clock = new FakeClock(morning);

        var (host, _) = SessionCircuit.Open(this.database, await CookieOfAsync(scenario.A.OwnerId), services => services.AddSingleton<TimeProvider>(clock));
        await using (host)
        {
            var permissions = host.Get<PermissionService>();
            await host.Get<UserSessionService>().InitializeFromClaimsAsync();
            Assert.True(await permissions.HasPermissionAsync(RolePermission.UsersView, cancellationToken));

            Guid holding;
            await using (var context = this.database.CreateSystemContext())
            {
                holding = await context.OrganizationUnitUserRoles
                    .Where(row => row.UserId == scenario.A.OwnerId && context.TenantRolePermissions.Any(grant => grant.TenantRoleId == row.TenantRoleId && grant.Permission == RolePermission.UsersView))
                    .Select(row => row.Id)
                    .SingleAsync(cancellationToken);
            }

            var removed = await host.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding);
            Assert.True(removed.IsSuccess, removed.Error);
            Assert.False(await permissions.HasPermissionAsync(RolePermission.UsersView, cancellationToken));
            Assert.True(await permissions.HasPermissionAsync(RolePermission.RolesEdit, cancellationToken));
        }
    }

    [Fact]
    public async Task An_open_circuit_stops_impersonating_within_one_revalidation_interval_after_the_impersonation_ended_in_another_tab()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var cookie = await StartAsync(await CookieOfAsync(platform.OwnerId), scenario.A.OwnerId);

        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();
            Assert.Equal((scenario.A.OwnerId, scenario.A.TenantId), (runtime.EffectiveUserId, runtime.TenantId));
            var changes = 0;
            session.OnChanged += () => Interlocked.Increment(ref changes);

            await EndAsync(cookie);
            Assert.True(runtime.IsImpersonating);

            var stillSignedIn = await RevalidationOf(host).RevalidateAsync(cookie, cancellationToken);

            Assert.True(stillSignedIn);
            Assert.Equal((false, (Guid?)null, (Guid?)null, true), (runtime.IsImpersonating, runtime.TenantId, runtime.EffectiveUserId, session.ImpersonationEnded));
            Assert.True(session.IsReady);
            Assert.Equal(1, changes);
        }

        Assert.Equal(TimeSpan.FromMinutes(5), IdentityRevalidatingAuthenticationStateProvider.Interval);
    }

    [Fact]
    public async Task Revalidation_ends_the_impersonation_of_an_owner_who_lost_platform_reach_and_closes_its_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var cookie = await StartAsync(await CookieOfAsync(platform.OwnerId), scenario.A.OwnerId);
        var sessionId = ImpersonationClaims.Read(cookie)!.SessionId;

        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();
            Assert.True(runtime.IsImpersonating);

            await RemoveAdminRoleAsync(platform.OwnerId, cancellationToken);
            Assert.True(await RevalidationOf(host).RevalidateAsync(cookie, cancellationToken));

            Assert.Equal((false, (Guid?)null, true), (runtime.IsImpersonating, runtime.TenantId, session.ImpersonationEnded));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.NotNull((await check.ImpersonationAudits.AsNoTracking().SingleAsync(row => row.Id == sessionId, cancellationToken)).Ended);
    }

    [Fact]
    public async Task Revalidation_drops_platform_reach_from_an_owner_who_lost_the_Admin_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var cookie = await CookieOfAsync(platform.OwnerId);

        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
        await using (host)
        {
            await host.Get<UserSessionService>().InitializeFromClaimsAsync();
            Assert.True(runtime.HasPlatformReach());

            await RemoveAdminRoleAsync(platform.OwnerId, cancellationToken);
            Assert.True(await RevalidationOf(host).RevalidateAsync(cookie, cancellationToken));

            Assert.False(runtime.HasPlatformReach());
            Assert.Equal((platform.OwnerId, platform.TenantId), (runtime.UserId, runtime.TenantId));
            Assert.False(await host.Get<PermissionService>().HasPermissionAsync(RolePermission.TenantView, cancellationToken));
        }
    }

    [Fact]
    public async Task Revalidation_signs_out_a_deactivated_user_and_a_user_whose_tenant_left_Active()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Company Revalidated", cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Member", cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var memberCookie = await CookieOfAsync(member);
        var ownerCookie = await CookieOfAsync(tenant.OwnerId);

        var (memberHost, memberRuntime) = SessionCircuit.Open(this.database, memberCookie);
        var (ownerHost, ownerRuntime) = SessionCircuit.Open(this.database, ownerCookie);
        await using (memberHost)
        await using (ownerHost)
        {
            await memberHost.Get<UserSessionService>().InitializeFromClaimsAsync();
            await ownerHost.Get<UserSessionService>().InitializeFromClaimsAsync();
            Assert.True(await RevalidationOf(memberHost).RevalidateAsync(memberCookie, cancellationToken));

            await UserEdits.SetStateInTableAsync(this.database, member, UserState.Inactive, cancellationToken);

            Assert.False(await RevalidationOf(memberHost).RevalidateAsync(memberCookie, cancellationToken));
            Assert.Equal((false, (Guid?)null, true), (memberRuntime.IsInitialized, memberRuntime.TenantId, memberHost.Get<UserSessionService>().IsReady));

            await using (var owner = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId)))
            {
                var suspended = await owner.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenant.TenantId, "tenant-suspend", cancellationToken: cancellationToken);
                Assert.True(suspended.IsSuccess, suspended.Error);
            }

            Assert.True(ownerRuntime.IsInitialized);
            Assert.False(await RevalidationOf(ownerHost).RevalidateAsync(ownerCookie, cancellationToken));
            Assert.Equal((false, (Guid?)null), (ownerRuntime.IsInitialized, ownerRuntime.TenantId));
        }
    }

    private static IdentityRevalidatingAuthenticationStateProvider RevalidationOf(ServiceHost circuit) =>
        new(NullLoggerFactory.Instance, circuit.Get<IServiceScopeFactory>(), circuit.Get<IServiceProvider>());

    private async Task<ClaimsPrincipal> CookieOfAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
        return await host.Get<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
    }

    private async Task<ClaimsPrincipal> StartAsync(ClaimsPrincipal cookie, Guid targetUserId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var started = await host.Get<ImpersonationService>().StartAsync(cookie, targetUserId, TestContext.Current.CancellationToken);
        Assert.True(started.IsSuccess, started.Error);
        return started.Value!;
    }

    private async Task EndAsync(ClaimsPrincipal cookie)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var ended = await host.Get<ImpersonationService>().EndAsync(cookie, TestContext.Current.CancellationToken);
        Assert.True(ended.IsSuccess, ended.Error);
    }

    private async Task RemoveAdminRoleAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var adminRole = await context.Roles.SingleAsync(role => role.Name == AppRoles.Admin, cancellationToken);
        context.UserRoles.Remove(await context.UserRoles.SingleAsync(holding => holding.UserId == userId && holding.RoleId == adminRole.Id, cancellationToken));
        await context.SaveChangesAsync(cancellationToken);
    }

    private sealed class HeldAuthenticationState : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal principal;

        public HeldAuthenticationState(ClaimsPrincipal principal)
        {
            this.principal = principal;
        }

        private readonly TaskCompletionSource<AuthenticationState> state = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => this.calls;

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            Interlocked.Increment(ref this.calls);
            return this.state.Task;
        }

        public void Release() => this.state.SetResult(new AuthenticationState(this.principal));
    }

    [Fact]
    public async Task A_request_naming_a_user_that_does_not_exist_reads_the_database_once_per_lifetime_not_once_per_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeClock(morning);
        var deleted = SessionCircuit.Cookie(Guid.NewGuid());
        var (host, _) = SessionCircuit.Open(this.database, deleted, services => services.AddSingleton<TimeProvider>(clock));
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            host.ClearCommandLog();

            for (var request = 0; request < 5; request++)
            {
                var identity = await session.DescribeRequestAsync(deleted, cancellationToken);
                Assert.Null(identity.TenantId);
            }

            Assert.Single(host.CommandLog);

            clock.Advance(UserSessionService.UnknownUserLifetime + TimeSpan.FromSeconds(1));
            await session.DescribeRequestAsync(deleted, cancellationToken);
            Assert.Equal(2, host.CommandLog.Count);
        }
    }
}
