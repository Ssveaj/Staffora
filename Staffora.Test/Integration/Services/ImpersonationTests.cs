using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class ImpersonationTests : IClassFixture<RealDatabase>
{
    private const string Password = "Password123!";
    private static readonly DateTimeOffset morning = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public ImpersonationTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task An_active_user_of_an_Active_tenant_is_authorized_and_a_circuit_of_the_reissued_cookie_acts_as_them_in_their_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        var cookie = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId);

        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();
            Assert.Equal((scenario.A.OwnerId, scenario.A.TenantId, platform.TenantId, platform.OwnerId), (runtime.EffectiveUserId, runtime.TenantId, runtime.BaseTenantId, runtime.UserId));
            Assert.Equal(ImpersonationClaims.Read(cookie)!.SessionId, session.ImpersonationSessionId);
            Assert.False(runtime.HasPlatformReach());
        }
    }

    [Fact]
    public async Task Impersonating_oneself_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await AssertRefusedAsync(platform.OwnerId, platform.OwnerId, ImpersonationAuthorizer.SelfRefused);
    }

    [Fact]
    public async Task A_user_who_is_not_a_platform_owner_is_refused_as_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var platformStaff = await TwoTenantScenario.AddUserAsync(this.database, platform, "Staff", cancellationToken);

        await AssertRefusedAsync(scenario.A.OwnerId, scenario.B.OwnerId, ImpersonationAuthorizer.NotPlatformOwner);
        await AssertRefusedAsync(platformStaff, scenario.B.OwnerId, ImpersonationAuthorizer.NotPlatformOwner);
    }

    [Fact]
    public async Task An_Admin_outside_the_platform_tenant_is_refused_as_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.A.OwnerId, cancellationToken);

        await AssertRefusedAsync(scenario.A.OwnerId, scenario.B.OwnerId, ImpersonationAuthorizer.NotPlatformOwner);
    }

    [Fact]
    public async Task A_platform_user_is_refused_as_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var otherOwner = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var platformStaff = await TwoTenantScenario.AddUserAsync(this.database, platform, "Staff", cancellationToken);
        var tenantAdmin = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Admin", cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, tenantAdmin, cancellationToken);

        await AssertRefusedAsync(platform.OwnerId, otherOwner.OwnerId, ImpersonationAuthorizer.PlatformUserRefused);
        await AssertRefusedAsync(platform.OwnerId, platformStaff, ImpersonationAuthorizer.PlatformUserRefused);
        await AssertRefusedAsync(platform.OwnerId, tenantAdmin, ImpersonationAuthorizer.PlatformUserRefused);
    }

    [Fact]
    public async Task A_system_account_is_refused_as_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await AssertRefusedAsync(platform.OwnerId, scenario.A.SystemAccountId, ImpersonationAuthorizer.SystemAccountRefused);
        await AssertRefusedAsync(platform.OwnerId, platform.SystemAccountId, ImpersonationAuthorizer.SystemAccountRefused);
    }

    [Fact]
    public async Task An_inactive_user_is_refused_as_target_and_as_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var inactiveOwner = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await DeactivateAsync(scenario.A.OwnerId, cancellationToken);
        await DeactivateAsync(inactiveOwner.OwnerId, cancellationToken);

        await AssertRefusedAsync(platform.OwnerId, scenario.A.OwnerId, ImpersonationAuthorizer.InactiveUserRefused);
        Assert.Equal(ImpersonationAuthorizer.NotPlatformOwner, await RefusalAsync(inactiveOwner.OwnerId, scenario.B.OwnerId, cancellationToken));
    }

    [Theory]
    [InlineData(TenantState.Setup)]
    [InlineData(TenantState.Suspended)]
    [InlineData(TenantState.PendingDeletion)]
    [InlineData(TenantState.Archived)]
    public async Task A_user_of_a_tenant_that_is_not_Active_is_refused_as_target(TenantState state)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, $"Company {state}", cancellationToken, state);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await AssertRefusedAsync(platform.OwnerId, tenant.OwnerId, ImpersonationAuthorizer.TenantNotActive);
    }

    [Fact]
    public async Task Starting_puts_the_targets_roles_and_its_audit_row_on_the_cookie_and_ending_puts_the_owners_roles_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GiveIdentityRoleAsync(this.database, scenario.A.OwnerId, "Coordinator role of A", cancellationToken);
        var signedIn = OwnerCookie(platform);
        signedIn.Identities.Single().AddClaim(new Claim("AspNet.Identity.SecurityStamp", "stamp"));

        var impersonating = await StartAsync(signedIn, scenario.A.OwnerId);

        var row = Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken));
        Assert.Equal(["Coordinator role of A"], RolesOf(impersonating));
        Assert.Equal(new ImpersonationClaim(scenario.A.OwnerId, row.Id), ImpersonationClaims.Read(impersonating));
        Assert.Equal((platform.OwnerId.ToString(), "stamp"), (impersonating.FindFirstValue(ClaimTypes.NameIdentifier), impersonating.FindFirstValue("AspNet.Identity.SecurityStamp")));

        var ended = await EndAsync(impersonating);

        Assert.Equal([AppRoles.Admin], RolesOf(ended));
        Assert.False(ImpersonationClaims.IsPresent(ended));
        Assert.Equal((platform.OwnerId.ToString(), "stamp"), (ended.FindFirstValue(ClaimTypes.NameIdentifier), ended.FindFirstValue("AspNet.Identity.SecurityStamp")));
        Assert.NotNull(Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken)).Ended);
    }

    [Fact]
    public async Task Starting_and_ending_impersonation_writes_one_audit_row_with_its_start_and_end()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var clock = new FakeClock(morning);

        var cookie = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId, clock);
        var running = Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken));
        Assert.Equal((scenario.A.OwnerId, scenario.A.TenantId, morning.UtcDateTime, (DateTime?)null), (running.TargetUserId, running.TargetTenantId, running.Started, running.Ended));

        clock.Advance(TimeSpan.FromMinutes(20));
        var ended = await EndAsync(cookie, clock);
        Assert.False((await SessionCircuit.ResolveAsync(this.database, ended)).IsImpersonating);

        var closed = Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken));
        Assert.Equal((morning.UtcDateTime, (DateTime?)morning.AddMinutes(20).UtcDateTime), (closed.Started, closed.Ended));
    }

    [Fact]
    public async Task Switching_target_closes_the_previous_audit_row_and_opens_one_for_the_new_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var clock = new FakeClock(morning);

        var first = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId, clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        var switched = await StartAsync(first, scenario.B.OwnerId, clock);

        var runtime = await SessionCircuit.ResolveAsync(this.database, switched);
        Assert.Equal((scenario.B.OwnerId, scenario.B.TenantId), (runtime.EffectiveUserId, runtime.TenantId));

        var audits = await AuditsOfAsync(platform.OwnerId, cancellationToken);
        Assert.Equal(2, audits.Count);
        Assert.Equal((scenario.A.OwnerId, morning.UtcDateTime, (DateTime?)morning.AddMinutes(5).UtcDateTime), (audits[0].TargetUserId, audits[0].Started, audits[0].Ended));
        Assert.Equal((scenario.B.OwnerId, morning.AddMinutes(5).UtcDateTime, (DateTime?)null), (audits[1].TargetUserId, audits[1].Started, audits[1].Ended));
        Assert.Equal(audits[1].Id, ImpersonationClaims.Read(switched)!.SessionId);
    }

    [Fact]
    public async Task Ending_with_no_session_open_writes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var clock = new FakeClock(morning);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), services => services.AddSingleton<TimeProvider>(clock));
        var writer = host.Get<ImpersonationAuditWriter>();
        var target = new ImpersonationTarget(scenario.A.OwnerId, null, null, scenario.A.TenantId);

        Assert.True((await writer.RecordEndAsync(platform.OwnerId, Guid.NewGuid(), cancellationToken)).IsSuccess);
        Assert.Empty(await AuditsOfAsync(platform.OwnerId, cancellationToken));

        var started = await writer.RecordStartAsync(platform.OwnerId, target, endingSessionId: null, cancellationToken);
        Assert.True(started.IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True((await writer.RecordEndAsync(platform.OwnerId, started.Value, cancellationToken)).IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(10));
        host.ClearCommandLog();
        Assert.True((await writer.RecordEndAsync(platform.OwnerId, started.Value, cancellationToken)).IsSuccess);

        Assert.DoesNotContain(host.CommandLog, IsWrite);
        var closed = Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken));
        Assert.Equal((started.Value, (DateTime?)morning.AddMinutes(10).UtcDateTime), (closed.Id, closed.Ended));
    }

    [Fact]
    public async Task Ending_a_cookie_whose_row_is_already_closed_writes_nothing_and_leaves_another_browsers_row_open()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var clock = new FakeClock(morning);

        var firstBrowser = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId, clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        await StartAsync(OwnerCookie(platform), scenario.B.OwnerId, clock);

        await CloseRowAsync(ImpersonationClaims.Read(firstBrowser)!.SessionId, morning.AddMinutes(7).UtcDateTime, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));
        await using (var endpoint = EndpointHost(clock))
        {
            endpoint.ClearCommandLog();
            var ended = await endpoint.Get<ImpersonationService>().EndAsync(firstBrowser, cancellationToken);
            Assert.True(ended.IsSuccess, ended.Error);
            Assert.False(ImpersonationClaims.IsPresent(ended.Value!));
            Assert.DoesNotContain(endpoint.CommandLog, IsWrite);
        }

        var audits = await AuditsOfAsync(platform.OwnerId, cancellationToken);
        Assert.Equal(
            [(scenario.A.OwnerId, (DateTime?)morning.AddMinutes(7).UtcDateTime), (scenario.B.OwnerId, null)],
            audits.Select(audit => (audit.TargetUserId, audit.Ended)));
    }

    [Fact]
    public async Task Ending_closes_exactly_its_own_row_while_another_browser_has_one_open()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var clock = new FakeClock(morning);

        var firstBrowser = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId, clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        var secondBrowser = await StartAsync(OwnerCookie(platform), scenario.B.OwnerId, clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        await EndAsync(firstBrowser, clock);

        var audits = await AuditsOfAsync(platform.OwnerId, cancellationToken);
        Assert.Equal(
            [(scenario.A.OwnerId, (DateTime?)morning.AddMinutes(10).UtcDateTime), (scenario.B.OwnerId, null)],
            audits.Select(audit => (audit.TargetUserId, audit.Ended)));
        Assert.Equal(scenario.B.OwnerId, (await SessionCircuit.ResolveAsync(this.database, secondBrowser)).EffectiveUserId);
    }

    [Fact]
    public async Task Switching_closes_exactly_its_own_row_while_another_browser_has_one_open()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var clock = new FakeClock(morning);

        var firstBrowser = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId, clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        await StartAsync(OwnerCookie(platform), scenario.B.OwnerId, clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        var switched = await StartAsync(firstBrowser, colleague, clock);
        Assert.Equal(colleague, (await SessionCircuit.ResolveAsync(this.database, switched)).EffectiveUserId);

        var audits = await AuditsOfAsync(platform.OwnerId, cancellationToken);
        Assert.Equal(
            [(scenario.A.OwnerId, (DateTime?)morning.AddMinutes(10).UtcDateTime), (scenario.B.OwnerId, null), (colleague, null)],
            audits.Select(audit => (audit.TargetUserId, audit.Ended)));
    }

    [Fact]
    public async Task Tabs_sharing_the_cookie_resolve_the_same_impersonation_and_an_old_copy_of_it_resumes_nothing_once_it_ended()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        var cookie = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId);
        var sessionId = ImpersonationClaims.Read(cookie)!.SessionId;
        foreach (var tab in new[] { "first tab", "second tab" })
        {
            var (host, runtime) = SessionCircuit.Open(this.database, cookie);
            await using (host)
            {
                var session = host.Get<UserSessionService>();
                await session.InitializeFromClaimsAsync();
                Assert.Equal((scenario.A.OwnerId, scenario.A.TenantId, (Guid?)sessionId), (runtime.EffectiveUserId, runtime.TenantId, session.ImpersonationSessionId));
            }
        }

        var ended = await EndAsync(cookie);
        var afterEnd = await SessionCircuit.ResolveAsync(this.database, ended);
        Assert.Equal((platform.OwnerId, platform.TenantId, false), (afterEnd.EffectiveUserId, afterEnd.TenantId, afterEnd.IsImpersonating));

        var (staleHost, staleRuntime) = SessionCircuit.Open(this.database, cookie);
        await using (staleHost)
        {
            var stale = staleHost.Get<UserSessionService>();
            await stale.InitializeFromClaimsAsync();
            Assert.Equal((false, (Guid?)null, (Guid?)null, true), (staleRuntime.IsInitialized, staleRuntime.TenantId, staleRuntime.EffectiveUserId, stale.ImpersonationEnded));
        }
    }

    [Fact]
    public async Task A_cookies_impersonation_is_applied_only_on_an_open_audit_row_of_that_owner_and_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var otherOwner = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        var aSession = ImpersonationClaims.Read(await StartAsync(OwnerCookie(platform), scenario.A.OwnerId))!.SessionId;
        var bSession = ImpersonationClaims.Read(await StartAsync(OwnerCookie(platform), scenario.B.OwnerId))!.SessionId;
        var otherOwnersSession = ImpersonationClaims.Read(await StartAsync(OwnerCookie(otherOwner), scenario.A.OwnerId))!.SessionId;

        var refused = new Dictionary<string, ClaimsPrincipal>
        {
            ["an ineligible target on another target's row"] = ImpersonationClaims.Reissue(OwnerCookie(platform), [], new ImpersonationClaim(scenario.A.SystemAccountId, aSession)),
            ["another target's row"] = ImpersonationClaims.Reissue(OwnerCookie(platform), [], new ImpersonationClaim(scenario.A.OwnerId, bSession)),
            ["another platform owner's row"] = ImpersonationClaims.Reissue(OwnerCookie(platform), [], new ImpersonationClaim(scenario.A.OwnerId, otherOwnersSession)),
            ["a row that does not exist"] = ImpersonationClaims.Reissue(OwnerCookie(platform), [], new ImpersonationClaim(scenario.A.OwnerId, Guid.NewGuid())),
            ["no row at all"] = WithClaims(OwnerCookie(platform), new Claim(ImpersonationClaims.TargetUserId, scenario.A.OwnerId.ToString())),
            ["a tenant user's cookie on the owner's row"] = ImpersonationClaims.Reissue(SessionCircuit.Cookie(scenario.B.OwnerId), [], new ImpersonationClaim(scenario.A.OwnerId, aSession)),
        };

        foreach (var (name, cookie) in refused)
        {
            var (host, runtime) = SessionCircuit.Open(this.database, cookie);
            await using (host)
            {
                var session = host.Get<UserSessionService>();
                await session.InitializeFromClaimsAsync();
                Assert.True((false, (Guid?)null, true) == (runtime.IsInitialized, runtime.TenantId, session.ImpersonationEnded), name);
            }
        }

        Assert.All(await AuditsOfAsync(platform.OwnerId, cancellationToken), audit => Assert.Null(audit.Ended));
        Assert.Null(Assert.Single(await AuditsOfAsync(otherOwner.OwnerId, cancellationToken)).Ended);
        var applied = await SessionCircuit.ResolveAsync(this.database, ImpersonationClaims.Reissue(OwnerCookie(platform), [], new ImpersonationClaim(scenario.A.OwnerId, aSession)));
        Assert.Equal((scenario.A.OwnerId, scenario.A.TenantId), (applied.EffectiveUserId, applied.TenantId));
    }

    [Fact]
    public async Task A_cookies_impersonation_whose_target_became_ineligible_is_not_applied_and_closes_only_its_own_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        var stale = await StartAsync(OwnerCookie(platform), colleague);
        var live = await StartAsync(OwnerCookie(platform), scenario.B.OwnerId);
        await DeactivateAsync(colleague, cancellationToken);

        var (host, runtime) = SessionCircuit.Open(this.database, stale);
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();
            Assert.Equal((false, (Guid?)null, true), (runtime.IsInitialized, runtime.EffectiveUserId, session.ImpersonationEnded));
        }

        Assert.Equal(scenario.B.OwnerId, (await SessionCircuit.ResolveAsync(this.database, live)).EffectiveUserId);
        var audits = await AuditsOfAsync(platform.OwnerId, cancellationToken);
        Assert.Equal([(colleague, true), (scenario.B.OwnerId, false)], audits.Select(audit => (audit.TargetUserId, audit.Ended is not null)));
    }

    [Fact]
    public async Task A_refused_impersonator_session_closes_its_own_open_row_and_leaves_another_owners_row_open()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var otherOwner = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        var refused = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId);
        var live = await StartAsync(OwnerCookie(otherOwner), scenario.A.OwnerId);
        await DeactivateAsync(platform.OwnerId, cancellationToken);

        var (host, runtime) = SessionCircuit.Open(this.database, refused);
        await using (host)
        {
            var session = host.Get<UserSessionService>();
            await session.InitializeFromClaimsAsync();
            Assert.Equal((false, (Guid?)null), (runtime.IsInitialized, runtime.EffectiveUserId));
        }

        Assert.NotNull(Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken)).Ended);
        Assert.Null(Assert.Single(await AuditsOfAsync(otherOwner.OwnerId, cancellationToken)).Ended);
        Assert.Equal(scenario.A.OwnerId, (await SessionCircuit.ResolveAsync(this.database, live)).EffectiveUserId);
    }

    [Fact]
    public async Task Ending_reports_a_failure_and_keeps_the_impersonation_when_its_audit_row_cannot_be_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var cookie = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId);

        await using (var context = this.database.CreateSystemContext())
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_ImpersonationAudits_RefuseUpdate ON ImpersonationAudits AFTER UPDATE AS THROW 50000, 'The audit store refused the update.', 1;", cancellationToken);
        }

        try
        {
            await using var endpoint = EndpointHost();
            var refused = await endpoint.Get<ImpersonationService>().EndAsync(cookie, cancellationToken);
            Assert.Equal((false, "The end of the impersonation could not be recorded."), (refused.IsSuccess, refused.Error));
            Assert.Null(Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken)).Ended);
        }
        finally
        {
            await using var context = this.database.CreateSystemContext();
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_ImpersonationAudits_RefuseUpdate;", CancellationToken.None);
        }

        Assert.False(ImpersonationClaims.IsPresent(await EndAsync(cookie)));
        Assert.NotNull(Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken)).Ended);
    }

    [Fact]
    public async Task A_refused_impersonator_session_stays_refused_and_logs_no_closure_when_its_audit_row_cannot_be_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var refused = await StartAsync(OwnerCookie(platform), scenario.A.OwnerId);
        await DeactivateAsync(platform.OwnerId, cancellationToken);

        await using (var context = this.database.CreateSystemContext())
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER TR_ImpersonationAudits_RefuseUpdate ON ImpersonationAudits AFTER UPDATE AS THROW 50000, 'The audit store refused the update.', 1;", cancellationToken);
        }

        var logged = new ConcurrentQueue<(LogLevel Level, string Message)>();
        try
        {
            var (host, runtime) = SessionCircuit.Open(this.database, refused, services => services.AddLogging(logging => logging.AddProvider(new SessionLog(logged))));
            await using (host)
            {
                await host.Get<UserSessionService>().InitializeFromClaimsAsync();
                Assert.Equal((false, (Guid?)null), (runtime.IsInitialized, runtime.EffectiveUserId));
            }
        }
        finally
        {
            await using var context = this.database.CreateSystemContext();
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_ImpersonationAudits_RefuseUpdate;", CancellationToken.None);
        }

        Assert.Null(Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken)).Ended);
        Assert.DoesNotContain(logged, line => line.Message.StartsWith("Closed impersonation session", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Level == LogLevel.Warning && line.Message.Contains("stays open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ending_is_refused_to_a_user_who_is_neither_impersonating_nor_a_platform_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await using var endpoint = EndpointHost();
        var impersonation = endpoint.Get<ImpersonationService>();
        Assert.Equal(ImpersonationAuthorizer.NotPlatformOwner, (await impersonation.EndAsync(SessionCircuit.Cookie(scenario.A.OwnerId), cancellationToken)).Error);
        Assert.Equal(ImpersonationAuthorizer.NotPlatformOwner, (await impersonation.EndAsync(SessionCircuit.Cookie(scenario.A.OwnerId, AppRoles.Admin), cancellationToken)).Error);
        Assert.Equal(ImpersonationService.NotSignedIn, (await impersonation.EndAsync(new ClaimsPrincipal(new ClaimsIdentity()), cancellationToken)).Error);

        var again = await impersonation.EndAsync(OwnerCookie(platform), cancellationToken);
        Assert.True(again.IsSuccess, again.Error);
        Assert.Equal([AppRoles.Admin], RolesOf(again.Value!));
    }

    [Fact]
    public async Task The_impersonation_dialog_lists_exactly_the_eligible_users_and_only_with_platform_reach()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var platformStaff = await TwoTenantScenario.AddUserAsync(this.database, platform, "Staff", cancellationToken);
        var inactive = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Inactive", cancellationToken);
        var tenantAdmin = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Admin", cancellationToken);
        var suspended = await TwoTenantScenario.AddTenantAsync(this.database, "Company Suspended", cancellationToken, TenantState.Suspended);
        await DeactivateAsync(inactive, cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, tenantAdmin, cancellationToken);

        var owner = await SessionCircuit.ResolveAsync(this.database, platform.OwnerId);
        IReadOnlyList<ImpersonationCandidate> listed;
        await using (var host = ServiceHost.Create(this.database, owner))
        {
            var result = await host.Get<ImpersonationService>().ListEligibleTargetsAsync(cancellationToken);
            Assert.True(result.IsSuccess, result.Error);
            listed = result.Value!;
        }

        Assert.Contains(listed, user => user.UserId == scenario.A.OwnerId && user.TenantName == scenario.A.Name);
        Assert.Contains(listed, user => user.UserId == scenario.B.OwnerId && user.TenantName == scenario.B.Name);
        foreach (var user in listed)
        {
            Assert.Null(await RefusalAsync(platform.OwnerId, user.UserId, cancellationToken));
        }

        var refusedKinds = new[] { platform.OwnerId, platformStaff, platform.SystemAccountId, scenario.A.SystemAccountId, inactive, tenantAdmin, suspended.OwnerId };
        Assert.DoesNotContain(listed, user => refusedKinds.Contains(user.UserId));

        var impersonating = await SessionCircuit.ResolveAsync(this.database, await StartAsync(OwnerCookie(platform), scenario.A.OwnerId));
        foreach (var runtime in new[] { impersonating, await SessionCircuit.ResolveAsync(this.database, scenario.A.OwnerId) })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            Assert.Equal(ImpersonationAuthorizer.NotPlatformOwner, (await host.Get<ImpersonationService>().ListEligibleTargetsAsync(cancellationToken)).Error);
        }
    }

    [Fact]
    public async Task A_platform_owner_impersonating_a_Tenant_A_user_is_refused_when_suspending_Tenant_A()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        var impersonating = await SessionCircuit.ResolveAsync(this.database, await StartAsync(OwnerCookie(platform), scenario.A.OwnerId));
        Assert.Equal((true, true), (impersonating.IsPlatformOwner(), impersonating.IsImpersonating));

        await using (var host = ServiceHost.Create(this.database, impersonating))
        {
            var refused = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(scenario.A.TenantId, "tenant-suspend", cancellationToken: cancellationToken);
            Assert.False(refused.IsSuccess);
            Assert.Equal("Only a platform owner may take the step 'Suspend'.", refused.Error);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(TenantState.Active, await check.Tenants.Where(tenant => tenant.Id == scenario.A.TenantId).Select(tenant => tenant.State).SingleAsync(cancellationToken));
        Assert.False(await check.StateHistoryEntries.AnyAsync(entry => entry.EntityId == scenario.A.TenantId, cancellationToken));
    }

    [Fact]
    public async Task The_impersonation_audit_is_append_only_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var otherOwner = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var longAgo = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Guid id;
        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var target = new ImpersonationTarget(scenario.A.OwnerId, null, null, scenario.A.TenantId);
            id = (await host.Get<ImpersonationAuditWriter>().RecordStartAsync(platform.OwnerId, target, endingSessionId: null, cancellationToken)).Value;
        }

        var before = await RowAsync(id, cancellationToken);

        await AssertRefusedUpdateAsync(id, row => { row.ImpersonatorId = otherOwner.OwnerId; row.Ended = longAgo; }, cancellationToken);
        await AssertRefusedUpdateAsync(id, row => row.TargetUserId = scenario.B.OwnerId, cancellationToken);
        await AssertRefusedUpdateAsync(id, row => row.TargetTenantId = scenario.B.TenantId, cancellationToken);
        await AssertRefusedUpdateAsync(id, row => { row.Started = longAgo; row.Ended = longAgo.AddHours(1); }, cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            system.ImpersonationAudits.Update(new ImpersonationAudit { Id = id, ImpersonatorId = otherOwner.OwnerId, TargetUserId = before.TargetUserId, TargetTenantId = before.TargetTenantId, Started = before.Started, Ended = longAgo });
            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        AssertSameRow(before, await RowAsync(id, cancellationToken));

        await using (var system = this.database.CreateSystemContext())
        {
            var rows = system.ImpersonationAudits.Where(audit => audit.Id == id);
            await Assert.ThrowsAsync<TenantIsolationException>(() => rows.ExecuteUpdateAsync(set => set.SetProperty(audit => audit.Ended, (DateTime?)null), cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => rows.ExecuteUpdateAsync(set => set.SetProperty(audit => audit.Ended, (DateTime?)longAgo), cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => rows.ExecuteDeleteAsync(cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => system.ImpersonationAudits.ExecuteDeleteAsync(cancellationToken));
        }

        AssertSameRow(before, await RowAsync(id, cancellationToken));

        await using (var system = this.database.CreateSystemContext())
        {
            var row = await system.ImpersonationAudits.SingleAsync(audit => audit.Id == id, cancellationToken);
            row.Ended = before.Started.AddMinutes(30);
            await system.SaveChangesAsync(cancellationToken);
        }

        var closed = await RowAsync(id, cancellationToken);
        Assert.Equal((DateTime?)before.Started.AddMinutes(30), closed.Ended);
        await AssertRefusedUpdateAsync(id, row => row.Ended = null, cancellationToken);
        await AssertRefusedUpdateAsync(id, row => row.Ended = longAgo, cancellationToken);
        AssertSameRow(closed, await RowAsync(id, cancellationToken));
    }

    [Fact]
    public async Task The_impersonation_audit_is_hidden_from_and_refused_to_every_context_outside_system_scope_and_never_deleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var target = new ImpersonationTarget(scenario.A.OwnerId, null, null, scenario.A.TenantId);
            Assert.True((await host.Get<ImpersonationAuditWriter>().RecordStartAsync(platform.OwnerId, target, endingSessionId: null, cancellationToken)).IsSuccess);
        }

        await using (var tenant = this.database.CreateContext(scenario.A.Runtime()))
        {
            Assert.False(await tenant.ImpersonationAudits.AnyAsync(cancellationToken));
            tenant.ImpersonationAudits.Add(new ImpersonationAudit { ImpersonatorId = scenario.A.OwnerId, TargetUserId = scenario.A.OwnerId, TargetTenantId = scenario.A.TenantId, Started = morning.UtcDateTime });
            await Assert.ThrowsAsync<TenantIsolationException>(() => tenant.SaveChangesAsync(cancellationToken));
        }

        await using (var console = this.database.CreateContext(platform.PlatformOwnerRuntime()))
        {
            Assert.False(await console.ImpersonationAudits.AnyAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var row = await system.ImpersonationAudits.SingleAsync(audit => audit.ImpersonatorId == platform.OwnerId, cancellationToken);
            system.ImpersonationAudits.Remove(row);
            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        Assert.Single(await AuditsOfAsync(platform.OwnerId, cancellationToken));
        Assert.Empty(await AuditsOfAsync(scenario.A.OwnerId, cancellationToken));
    }

    [Fact]
    public async Task A_row_written_while_impersonating_records_the_target_as_creator_and_the_platform_owner_as_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var runtime = await SessionCircuit.ResolveAsync(this.database, await StartAsync(OwnerCookie(platform), scenario.A.OwnerId));

        Guid id;
        await using (var context = this.database.CreateContext(runtime))
        {
            var parent = new TestParent { Name = "Written while impersonating" };
            context.TestParents.Add(parent);
            await context.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
        Assert.Equal((scenario.A.TenantId, scenario.A.OwnerId, (Guid?)platform.OwnerId), (stored.TenantId, stored.CreatedById, stored.ImpersonatedById));
    }

    [Fact]
    public async Task A_user_created_through_UserService_while_impersonating_records_the_target_as_creator_and_the_platform_owner_as_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersCreate);
        var email = $"created.{Guid.NewGuid():N}@company-a.example";

        var (host, _) = SessionCircuit.Open(this.database, await StartAsync(OwnerCookie(platform), scenario.A.OwnerId));
        await using (host)
        {
            await host.Get<UserSessionService>().InitializeFromClaimsAsync();
            var created = await host.Get<UserService>().CreateUserAsync(new ApplicationUser { UserName = email, Email = email, FirstName = "Created", LastName = "User" }, scenario.A.TenantId, Password);
            Assert.True(created.IsSuccess, created.Error);
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.Users.AsNoTracking().SingleAsync(user => user.Email == email, cancellationToken);
        Assert.Equal((scenario.A.TenantId, scenario.A.OwnerId, (Guid?)platform.OwnerId), (stored.TenantId, stored.CreatedById, stored.ImpersonatedById));
    }

    private async Task AssertRefusedAsync(Guid actingUserId, Guid targetUserId, string reason)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(reason, await RefusalAsync(actingUserId, targetUserId, cancellationToken));

        await using (var endpoint = EndpointHost())
        {
            var started = await endpoint.Get<ImpersonationService>().StartAsync(SessionCircuit.Cookie(actingUserId, AppRoles.Admin), targetUserId, cancellationToken);
            Assert.Equal(reason, started.Error);
            Assert.Null(started.Value);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.ImpersonationAudits.AnyAsync(audit => audit.ImpersonatorId == actingUserId && audit.TargetUserId == targetUserId, cancellationToken));
    }

    private async Task<string?> RefusalAsync(Guid actingUserId, Guid targetUserId, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var result = await host.Get<ImpersonationAuthorizer>().AuthorizeAsync(actingUserId, targetUserId, cancellationToken);
        return result.IsSuccess ? null : result.Error;
    }

    private ServiceHost EndpointHost(TimeProvider? clock = null) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        });

    private async Task<ClaimsPrincipal> StartAsync(ClaimsPrincipal cookie, Guid targetUserId, TimeProvider? clock = null)
    {
        await using var endpoint = EndpointHost(clock);
        var started = await endpoint.Get<ImpersonationService>().StartAsync(cookie, targetUserId, TestContext.Current.CancellationToken);
        Assert.True(started.IsSuccess, started.Error);
        return started.Value!;
    }

    private async Task<ClaimsPrincipal> EndAsync(ClaimsPrincipal cookie, TimeProvider? clock = null)
    {
        await using var endpoint = EndpointHost(clock);
        var ended = await endpoint.Get<ImpersonationService>().EndAsync(cookie, TestContext.Current.CancellationToken);
        Assert.True(ended.IsSuccess, ended.Error);
        return ended.Value!;
    }

    private static ClaimsPrincipal OwnerCookie(ScenarioTenant platform) => SessionCircuit.Cookie(platform.OwnerId, AppRoles.Admin);

    private static List<string> RolesOf(ClaimsPrincipal cookie) => [.. cookie.FindAll(ClaimTypes.Role).Select(claim => claim.Value)];

    private static ClaimsPrincipal WithClaims(ClaimsPrincipal cookie, params Claim[] claims)
    {
        var identity = new ClaimsIdentity(cookie.Claims.Concat(claims), "Test");
        return new ClaimsPrincipal(identity);
    }

    private Task DeactivateAsync(Guid userId, CancellationToken cancellationToken) =>
        UserEdits.SetStateInTableAsync(this.database, userId, UserState.Inactive, cancellationToken);

    private static bool IsWrite(string command) =>
        command.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) || command.Contains("INSERT", StringComparison.OrdinalIgnoreCase);

    private async Task CloseRowAsync(Guid sessionId, DateTime ended, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var row = await context.ImpersonationAudits.SingleAsync(audit => audit.Id == sessionId, cancellationToken);
        row.Ended = ended;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task AssertRefusedUpdateAsync(Guid id, Action<ImpersonationAudit> change, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var row = await system.ImpersonationAudits.SingleAsync(audit => audit.Id == id, cancellationToken);
        change(row);
        await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
    }

    private async Task<ImpersonationAudit> RowAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ImpersonationAudits.AsNoTracking().SingleAsync(audit => audit.Id == id, cancellationToken);
    }

    private static void AssertSameRow(ImpersonationAudit expected, ImpersonationAudit actual) =>
        Assert.Equal(
            (expected.ImpersonatorId, expected.TargetUserId, expected.TargetTenantId, expected.Started, expected.Ended),
            (actual.ImpersonatorId, actual.TargetUserId, actual.TargetTenantId, actual.Started, actual.Ended));

    private async Task<List<ImpersonationAudit>> AuditsOfAsync(Guid impersonatorId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ImpersonationAudits.AsNoTracking().Where(audit => audit.ImpersonatorId == impersonatorId).OrderBy(audit => audit.Started).ToListAsync(cancellationToken);
    }

    private sealed class SessionLog : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> lines;

        public SessionLog(ConcurrentQueue<(LogLevel Level, string Message)> lines)
        {
            this.lines = lines;
        }

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(UserSessionService).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            this.lines.Enqueue((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }
}
