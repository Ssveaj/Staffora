using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Blazor.Components.Account;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Services.Authorization;
using Staffora.Services.Comments;
using Staffora.Services.Messaging;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class TenantLifecycleAccessTests : IClassFixture<RealDatabase>
{
    private const string Password = "Password123!";
    private const string Activate = "test-parent-activate";

    private readonly RealDatabase database;

    public TenantLifecycleAccessTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_suspended_tenants_user_is_refused_at_sign_in_at_the_next_request_and_circuit_an_open_circuit_at_revalidation_and_reactivation_restores_access()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        var cookie = await CookieOfAsync(tenant.OwnerId);
        Assert.True(await SignsInAsync(tenant.OwnerId));
        Assert.True(await AdmitsAsync(cookie));

        var (open, openRuntime) = SessionCircuit.Open(this.database, cookie);
        await using (open)
        {
            await open.Get<UserSessionService>().InitializeFromClaimsAsync();
            Assert.Equal(tenant.OwnerId, openRuntime.UserId);

            await MoveAsync(platform, tenant.TenantId, "tenant-suspend", cancellationToken);

            Assert.False(await SignsInAsync(tenant.OwnerId));
            Assert.False(await AdmitsAsync(cookie));
            AssertNoSession(await SessionCircuit.ResolveAsync(this.database, cookie));

            Assert.Equal(tenant.OwnerId, openRuntime.UserId);
            Assert.False(await RevalidationOf(open).RevalidateAsync(cookie, cancellationToken));
            AssertNoSession(openRuntime);
        }

        Assert.Equal(TimeSpan.FromMinutes(5), IdentityRevalidatingAuthenticationStateProvider.Interval);

        await MoveAsync(platform, tenant.TenantId, "tenant-reactivate", cancellationToken);
        Assert.True(await SignsInAsync(tenant.OwnerId));
        Assert.False(await AdmitsAsync(cookie));
        AssertNoSession(await SessionCircuit.ResolveAsync(this.database, cookie));
        var fresh = await CookieOfAsync(tenant.OwnerId);
        Assert.True(await AdmitsAsync(fresh));
        Assert.Equal(tenant.OwnerId, (await SessionCircuit.ResolveAsync(this.database, fresh)).UserId);
    }

    [Theory]
    [InlineData("tenant-suspend")]
    [InlineData("tenant-schedule-deletion")]
    public async Task A_step_that_stops_the_tenants_sign_in_rotates_every_users_stamps_so_no_cookie_from_before_comes_back_after_reactivation(string step)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Colleague", cancellationToken);
        var other = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Company Other"), cancellationToken);
        var cookies = new[] { await CookieOfAsync(tenant.OwnerId), await CookieOfAsync(colleague) };
        var otherCookie = await CookieOfAsync(other.OwnerId);
        var before = await StampsAsync(tenant.TenantId, cancellationToken);

        await MoveAsync(platform, tenant.TenantId, step, cancellationToken);

        var after = await StampsAsync(tenant.TenantId, cancellationToken);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(after, pair => Assert.True(pair.Value.Security != before[pair.Key].Security && pair.Value.Concurrency != before[pair.Key].Concurrency, $"user {pair.Key} kept a stamp"));
        Assert.All(after, pair => Assert.Equal(tenant.SystemAccountId, pair.Value.EditedBy));
        Assert.Equal(after.Count, after.Values.Select(stamps => stamps.Security).Distinct().Count());
        Assert.True(await AdmitsAsync(otherCookie));

        if (step == "tenant-schedule-deletion")
        {
            await MoveAsync(platform, tenant.TenantId, "tenant-cancel-deletion", cancellationToken);
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-reactivate", cancellationToken);
        foreach (var cookie in cookies)
        {
            Assert.False(await AdmitsAsync(cookie));
        }

        Assert.True(await SignsInAsync(tenant.OwnerId));
        Assert.True(await AdmitsAsync(await CookieOfAsync(tenant.OwnerId)));
    }

    [Fact]
    public async Task An_impersonation_into_a_suspended_tenant_is_refused_a_live_one_ends_on_the_next_circuit_while_the_owners_own_cookie_stands_and_reactivation_allows_it_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        var target = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Target", cancellationToken);
        var ownerCookie = await CookieOfAsync(platform.OwnerId);
        var impersonating = await StartAsync(ownerCookie, tenant.OwnerId);
        var sessionId = ImpersonationClaims.Read(impersonating)!.SessionId;

        var (open, openRuntime) = SessionCircuit.Open(this.database, impersonating);
        await using (open)
        {
            var openSession = open.Get<UserSessionService>();
            await openSession.InitializeFromClaimsAsync();
            Assert.Equal((tenant.OwnerId, tenant.TenantId), (openRuntime.EffectiveUserId, openRuntime.TenantId));

            await MoveAsync(platform, tenant.TenantId, "tenant-suspend", cancellationToken);

            Assert.Equal(ImpersonationAuthorizer.TenantNotActive, await StartRefusalAsync(ownerCookie, target, cancellationToken));

            Assert.True(await AdmitsAsync(impersonating));

            var (next, nextRuntime) = SessionCircuit.Open(this.database, impersonating);
            await using (next)
            {
                var nextSession = next.Get<UserSessionService>();
                await nextSession.InitializeFromClaimsAsync();
                Assert.True(nextSession.ImpersonationEnded);
                Assert.Equal((false, (Guid?)null, (Guid?)null), (nextRuntime.IsImpersonating, nextRuntime.TenantId, nextRuntime.EffectiveUserId));
            }

            Assert.NotNull((await AuditRowAsync(sessionId, cancellationToken)).Ended);

            Assert.True(openRuntime.IsImpersonating);
            Assert.True(await RevalidationOf(open).RevalidateAsync(impersonating, cancellationToken));
            Assert.Equal((false, (Guid?)null, true), (openRuntime.IsImpersonating, openRuntime.TenantId, openSession.ImpersonationEnded));
        }

        var ended = await EndAsync(impersonating);
        var console = await SessionCircuit.ResolveAsync(this.database, ended);
        Assert.Equal((platform.OwnerId, platform.TenantId, true, false), (console.UserId, console.TenantId, console.HasPlatformReach(), console.IsImpersonating));

        await MoveAsync(platform, tenant.TenantId, "tenant-reactivate", cancellationToken);
        Assert.Null(await StartRefusalAsync(ended, target, cancellationToken));
        var again = await SessionCircuit.ResolveAsync(this.database, await StartAsync(ended, target));
        Assert.Equal((target, tenant.TenantId, true), (again.EffectiveUserId, again.TenantId, again.IsImpersonating));
    }

    [Fact]
    public async Task A_tenant_in_Pending_deletion_or_Archived_refuses_sign_in_every_cookie_and_impersonation_and_cancelling_the_deletion_leaves_it_suspended()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        var cookie = await CookieOfAsync(tenant.OwnerId);
        var ownerCookie = await CookieOfAsync(platform.OwnerId);

        foreach (var (step, state) in new[]
        {
            ("tenant-schedule-deletion", TenantState.PendingDeletion),
            ("tenant-cancel-deletion", TenantState.Suspended),
            ("tenant-schedule-deletion-suspended", TenantState.PendingDeletion),
            ("tenant-archive", TenantState.Archived),
        })
        {
            await MoveAsync(platform, tenant.TenantId, step, cancellationToken);
            Assert.Equal(state, await StateOfAsync(tenant.TenantId, cancellationToken));

            Assert.False(await SignsInAsync(tenant.OwnerId), $"sign-in in {state}");
            Assert.False(await AdmitsAsync(cookie), $"the cookie in {state}");
            AssertNoSession(await SessionCircuit.ResolveAsync(this.database, cookie));
            Assert.Equal(ImpersonationAuthorizer.TenantNotActive, await StartRefusalAsync(ownerCookie, tenant.OwnerId, cancellationToken));
        }

        Assert.True(await AdmitsAsync(ownerCookie));
        Assert.True((await SessionCircuit.ResolveAsync(this.database, ownerCookie)).HasPlatformReach());
    }

    [Fact]
    public async Task No_step_changes_a_held_tenants_records_whoever_asks_while_the_tenants_own_steps_stay_open_and_reactivation_opens_them_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        var parent = await AddParentAsync(tenant, cancellationToken);
        var console = await SessionCircuit.ResolveAsync(this.database, platform.OwnerId);

        foreach (var (step, state) in new[] { ("tenant-suspend", TenantState.Suspended), ("tenant-schedule-deletion-suspended", TenantState.PendingDeletion), ("tenant-cancel-deletion", TenantState.Suspended) })
        {
            await MoveAsync(platform, tenant.TenantId, step, cancellationToken);

            foreach (var runtime in new[] { console, tenant.Runtime() })
            {
                await using var host = ServiceHost.Create(this.database, runtime);
                var workflows = host.Get<WorkflowService>();
                Assert.Equal(WorkflowService.HeldTenant, (await workflows.TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken)).Error);

                var footer = await workflows.GetAvailableTransitionsAsync<TestParentState, TestParent>(parent, cancellationToken: cancellationToken);
                Assert.True(footer.IsSuccess, footer.Error);
                Assert.All(footer.Value!, step => Assert.NotNull(step.AccessRefusal));
                Assert.Equal(WorkflowService.HeldTenant, footer.Value!.Single(step => step.Transition.Key == Activate).AccessRefusal);
            }

            await using (var member = ServiceHost.Create(this.database, tenant.Runtime()))
            {
                var workflows = member.Get<WorkflowService>();
                Assert.Equal(WorkflowService.HeldTenant, (await workflows.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = "Held" }, "test-parent-save", cancellationToken: cancellationToken)).Error);
                var createFooter = await workflows.GetTransitionAccessAsync<TestParentState, TestParent>(TestParentState.Draft, cancellationToken);
                Assert.All(createFooter.Value!, step => Assert.NotNull(step.AccessRefusal));
                Assert.Equal(WorkflowService.HeldTenant, createFooter.Value!.Single(step => step.Transition.Key == "test-parent-save").AccessRefusal);
            }

            Assert.Equal(state, await StateOfAsync(tenant.TenantId, cancellationToken));
        }

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.Equal(TestParentState.Draft, (await check.TestParents.AsNoTracking().SingleAsync(row => row.Id == parent, cancellationToken)).State);
            Assert.False(await check.TestParents.AnyAsync(row => row.TenantId == tenant.TenantId && row.Name == "Held", cancellationToken));
            Assert.False(await check.StateHistoryEntries.AnyAsync(entry => entry.EntityId == parent, cancellationToken));
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-reactivate", cancellationToken);
        await using var after = ServiceHost.Create(this.database, tenant.Runtime());
        var activated = await after.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);
    }

    [Fact]
    public async Task An_archived_tenants_record_takes_no_step_from_the_console_and_the_footer_shows_why()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        var parent = await AddParentAsync(tenant, cancellationToken);
        await MoveAsync(platform, tenant.TenantId, "tenant-schedule-deletion", cancellationToken);
        await MoveAsync(platform, tenant.TenantId, "tenant-archive", cancellationToken);
        Assert.Equal(TenantState.Archived, await StateOfAsync(tenant.TenantId, cancellationToken));

        await using var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId));
        var workflows = host.Get<WorkflowService>();
        Assert.Equal(WorkflowService.HeldTenant, (await workflows.TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken)).Error);

        var answers = await workflows.GetAvailableTransitionsAsync<TestParentState, TestParent>(parent, cancellationToken: cancellationToken);
        Assert.True(answers.IsSuccess, answers.Error);
        Assert.Equal(WorkflowService.HeldTenant, answers.Value!.Single(answer => answer.Transition.Key == Activate).AccessRefusal);

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(TestParentState.Draft, (await check.TestParents.AsNoTracking().SingleAsync(row => row.Id == parent, cancellationToken)).State);
    }

    [Theory]
    [InlineData("role holding given")]
    [InlineData("role holding taken away")]
    [InlineData("setting saved")]
    [InlineData("notices marked read")]
    [InlineData("comment posted")]
    public async Task A_suspended_tenants_live_circuit_writes_nothing_outside_a_step_either_and_is_told_why(string writer)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, tenant, tenant.OwnerId, cancellationToken,
            RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.SettingsView, RolePermission.SettingsEdit, TestParentDialogService.InternalThreadPermission);
        var target = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Target", cancellationToken);
        var root = await TwoTenantScenario.RootUnitIdAsync(this.database, tenant, cancellationToken);

        await TwoTenantScenario.GrantAsync(this.database, tenant, tenant.OwnerId, cancellationToken, RolePermission.RolesView);
        Guid smallRole;
        await using (var read = this.database.CreateSystemContext())
        {
            smallRole = await read.TenantRoles.Where(role => role.TenantId == tenant.TenantId && role.Permissions.Count == 1).Select(role => role.Id).SingleAsync(cancellationToken);
        }

        var parent = await AddParentAsync(tenant, cancellationToken);
        Guid? holding = null;
        await using (var active = ServiceHost.Create(this.database, tenant.Runtime()))
        {
            if (writer == "role holding taken away")
            {
                var given = await active.Get<RoleHoldingService>().AssignUserToRoleAsync(target, root, smallRole, tenant.TenantId);
                Assert.True(given.IsSuccess, given.Error);
                holding = given.Value!.Id;
            }
            else if (writer == "notices marked read")
            {
                var activated = await active.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken);
                Assert.True(activated.IsSuccess, activated.Error);
                Assert.Equal(1, (await active.Get<NoticeService>().GetUnreadCountAsync(cancellationToken)).Value);
            }
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-suspend", cancellationToken);

        await using var circuit = ServiceHost.Create(this.database, tenant.Runtime());
        var refusal = writer switch
        {
            "role holding given" => (await circuit.Get<RoleHoldingService>().AssignUserToRoleAsync(target, root, smallRole, tenant.TenantId)).Error,
            "role holding taken away" => (await circuit.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding!.Value)).Error,
            "setting saved" => await SaveTimeZoneAsync(circuit.Get<TenantSettingsService>(), cancellationToken),
            "notices marked read" => (await circuit.Get<NoticeService>().MarkAllReadAsync(cancellationToken)).Error,
            "comment posted" => (await circuit.Get<CommentService>().AddCommentAsync<TestParent>(parent, TestParentDialogService.CommentAccess[0], "Held?", cancellationToken)).Error,
            _ => throw new ArgumentOutOfRangeException(nameof(writer)),
        };

        Assert.Equal(TenantStates.HeldDataReason, refusal);

        await using var check = this.database.CreateSystemContext();
        switch (writer)
        {
            case "role holding given":
                Assert.False(await check.OrganizationUnitUserRoles.AnyAsync(row => row.UserId == target, cancellationToken));
                break;
            case "role holding taken away":
                Assert.True(await check.OrganizationUnitUserRoles.AnyAsync(row => row.Id == holding, cancellationToken));
                break;
            case "setting saved":
                Assert.False(await check.TenantSettings.AnyAsync(row => row.TenantId == tenant.TenantId && row.Value == "America/New_York", cancellationToken));
                break;
            case "notices marked read":
                Assert.False((await check.MessageRecipients.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId && row.UserId == tenant.OwnerId, cancellationToken)).IsRead);
                break;
            case "comment posted":
                Assert.False(await check.Comments.AnyAsync(row => row.TenantId == tenant.TenantId, cancellationToken));
                break;
        }
    }

    [Fact]
    public async Task A_step_and_a_suspension_racing_never_commit_the_step_after_the_suspension()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        for (var round = 0; round < 5; round++)
        {
            var fromConsole = round % 2 == 1;
            var stepFirst = round / 2 % 2 == 0;
            var parent = await AddParentAsync(tenant, cancellationToken);
            var pause = new PauseFirstCommit();
            await using var stepping = ServiceHost.Create(this.database, fromConsole ? await SessionCircuit.ResolveAsync(this.database, platform.OwnerId) : tenant.Runtime(), interceptors: stepFirst ? [pause] : []);
            await using var moving = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId), interceptors: stepFirst ? [] : [pause]);
            Func<Task<Result>> step = () => stepping.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken);
            Func<Task<Result>> suspend = () => moving.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenant.TenantId, "tenant-suspend", cancellationToken: cancellationToken);

            var first = Task.Run(stepFirst ? step : suspend, cancellationToken);
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var second = Task.Run(stepFirst ? suspend : step, cancellationToken);
            var passedTheHeldCommit = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken)) == second;
            pause.Release.TrySetResult();
            var (stepped, suspended) = stepFirst ? (await first, await second) : (await second, await first);

            var label = $"round {round} ({(fromConsole ? "console" : "live circuit")}, {(stepFirst ? "step" : "suspension")} first)";
            Assert.False(passedTheHeldCommit, $"{label}: the second committed past the first's held commit");
            Assert.True(suspended.IsSuccess, $"{label}: {suspended.Error}");
            if (stepFirst)
            {
                Assert.True(stepped.IsSuccess, $"{label}: {stepped.Error}");
            }
            else
            {
                Assert.Equal(WorkflowService.HeldTenant, stepped.Error);
            }

            await using (var check = this.database.CreateSystemContext())
            {
                Assert.Equal(stepFirst ? TestParentState.Active : TestParentState.Draft, (await check.TestParents.AsNoTracking().SingleAsync(row => row.Id == parent, cancellationToken)).State);
            }

            await MoveAsync(platform, tenant.TenantId, "tenant-reactivate", cancellationToken);
        }
    }

    [Fact]
    public async Task A_tenant_transaction_holding_a_user_row_and_a_suspension_never_deadlock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        await using var writer = this.database.CreateContext(tenant.Runtime());
        Task<Result> suspension;
        bool suspendedMeanwhile;
        await using (var transaction = await writer.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken))
        {
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [AspNetUsers] WITH (HOLDLOCK, ROWLOCK) WHERE [Id] = {tenant.OwnerId}", cancellationToken);

            await using var moving = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId));
            suspension = Task.Run(() => moving.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenant.TenantId, "tenant-suspend", cancellationToken: cancellationToken), cancellationToken);
            suspendedMeanwhile = await Task.WhenAny(suspension, Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken)) == suspension;

            writer.TestParents.Add(new TestParent { Name = "Begun before the suspension", State = TestParentState.Draft });
            await writer.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var suspended = await suspension;
            Assert.True(suspended.IsSuccess, suspended.Error);
        }

        Assert.False(suspendedMeanwhile, "the suspension went past a transaction of the tenant that had begun before it");
        Assert.Equal(TenantState.Suspended, await StateOfAsync(tenant.TenantId, cancellationToken));
    }

    [Fact]
    public async Task A_locked_account_of_a_suspended_tenant_reads_as_locked_even_with_the_right_password()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        await using (var setup = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var users = setup.Get<UserManager<ApplicationUser>>();
            Assert.True((await users.SetLockoutEnabledAsync((await users.FindByIdAsync(tenant.OwnerId.ToString()))!, true)).Succeeded);
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-suspend", cancellationToken);

        var last = SignInResult.Failed;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            last = await CheckPasswordAsync(tenant.OwnerId, $"Wrong-{attempt}", lockoutOnFailure: true);
        }

        Assert.True(last.IsLockedOut);

        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var manager = (ApplicationSignInManager)host.Get<SignInManager<ApplicationUser>>();
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(tenant.OwnerId.ToString()))!;
        var right = await manager.CheckPasswordSignInAsync(user, Password, lockoutOnFailure: true);
        Assert.True(right.IsLockedOut);
        Assert.Equal(SignInRefusal.None, manager.Refusal);
    }

    [Fact]
    public async Task A_tenant_in_Setup_is_not_held_so_its_records_take_their_steps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var setup = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Company Setup"), cancellationToken, TenantState.Setup);
        var parent = await AddParentAsync(setup, cancellationToken);

        await using var host = ServiceHost.Create(this.database, setup.Runtime());
        var activated = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);
    }

    [Fact]
    public async Task A_notice_of_a_suspended_tenant_stays_pending_and_is_delivered_once_the_tenant_is_Active_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (tenant, platform) = await TenantAndOwnerAsync(cancellationToken);
        var parent = await AddParentAsync(tenant, cancellationToken);

        await using (var stepping = ServiceHost.Create(this.database, tenant.Runtime(), services =>
        {
            services.RemoveAll<IOutgoingMessageDispatcher>();
            services.AddScoped<IOutgoingMessageDispatcher, NotYetDispatched>();
        }))
        {
            var activated = await stepping.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken);
            Assert.True(activated.IsSuccess, activated.Error);
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-suspend", cancellationToken);
        await DispatchAsync(tenant, parent, cancellationToken);

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.Equal(OutgoingMessageStatus.Pending, (await check.OutgoingMessages.AsNoTracking().SingleAsync(row => row.EntityId == parent, cancellationToken)).Status);
            Assert.False(await check.StoredMessages.AnyAsync(row => row.EntityId == parent, cancellationToken));
            Assert.False(await check.MessageRecipients.AnyAsync(row => row.TenantId == tenant.TenantId, cancellationToken));
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-reactivate", cancellationToken);
        await DispatchAsync(tenant, parent, cancellationToken);
        await using var after = this.database.CreateSystemContext();
        Assert.Equal(OutgoingMessageStatus.Delivered, (await after.OutgoingMessages.AsNoTracking().SingleAsync(row => row.EntityId == parent, cancellationToken)).Status);
        Assert.Equal(tenant.OwnerId, (await after.MessageRecipients.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId, cancellationToken)).UserId);
    }

    private async Task<(ScenarioTenant Tenant, ScenarioTenant Platform)> TenantAndOwnerAsync(CancellationToken cancellationToken)
    {
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Company Lifecycle"), cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await using var setup = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var users = setup.Get<UserManager<ApplicationUser>>();
        Assert.True((await users.AddPasswordAsync((await users.FindByIdAsync(tenant.OwnerId.ToString()))!, Password)).Succeeded);
        return (tenant, platform);
    }

    private async Task MoveAsync(ScenarioTenant platform, Guid tenantId, string step, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId));
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, step, cancellationToken: cancellationToken);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private async Task<TenantState> StateOfAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.State).SingleAsync(cancellationToken);
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, CancellationToken cancellationToken)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", State = TestParentState.Draft };
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<Dictionary<Guid, (string? Security, string? Concurrency, Guid? EditedBy)>> StampsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: read a tenant's users' stamps");
        return await context.Users.AsNoTracking()
            .Where(user => user.TenantId == tenantId)
            .ToDictionaryAsync(user => user.Id, user => (user.SecurityStamp, user.ConcurrencyStamp, user.ModifiedById), cancellationToken);
    }

    private static async Task<string?> SaveTimeZoneAsync(TenantSettingsService settings, CancellationToken cancellationToken)
    {
        var loaded = await settings.LoadAsync(cancellationToken);
        Assert.True(loaded.IsSuccess, loaded.Error);
        var zone = loaded.Value!.Of(TenantSettingDefinitions.TimeZone);
        return (await settings.SaveAsync(TenantSettingDefinitions.TimeZone.Key, "America/New_York", zone.Version, cancellationToken)).Error;
    }

    private async Task<SignInResult> CheckPasswordAsync(Guid userId, string password, bool lockoutOnFailure)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
        return await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(user, password, lockoutOnFailure);
    }

    private async Task DispatchAsync(ScenarioTenant tenant, Guid parent, CancellationToken cancellationToken)
    {
        List<OutgoingMessage> rows;
        await using (var reader = this.database.CreateSystemContext())
        {
            rows = await reader.OutgoingMessages.AsNoTracking().Where(row => row.EntityId == parent).ToListAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, tenant.Runtime());
        var dispatcher = host.Get<IOutgoingMessageDispatcher>();
        Assert.IsType<OutgoingMessageDispatcher>(dispatcher);
        await dispatcher.DispatchAsync(rows, rows[0].HistoryEntryId, cancellationToken);
    }

    private async Task<ClaimsPrincipal> CookieOfAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
        return await host.Get<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
    }

    private async Task<bool> AdmitsAsync(ClaimsPrincipal cookie)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        return await ((ApplicationSignInManager)host.Get<SignInManager<ApplicationUser>>()).AdmitsCookieAsync(cookie, TestContext.Current.CancellationToken);
    }

    private async Task<bool> SignsInAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
        var result = await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(user, Password, lockoutOnFailure: false);
        Assert.False(result.IsLockedOut);
        return result.Succeeded;
    }

    private ServiceHost EndpointHost() => ServiceHost.Create(this.database, new RuntimeExecutionContext());

    private async Task<ClaimsPrincipal> StartAsync(ClaimsPrincipal cookie, Guid targetUserId)
    {
        await using var endpoint = EndpointHost();
        var started = await endpoint.Get<ImpersonationService>().StartAsync(cookie, targetUserId, TestContext.Current.CancellationToken);
        Assert.True(started.IsSuccess, started.Error);
        return started.Value!;
    }

    private async Task<string?> StartRefusalAsync(ClaimsPrincipal cookie, Guid targetUserId, CancellationToken cancellationToken)
    {
        await using var endpoint = EndpointHost();
        var actingUserId = Guid.Parse(cookie.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var authorized = await endpoint.Get<ImpersonationAuthorizer>().AuthorizeAsync(actingUserId, targetUserId, cancellationToken);
        if (authorized.IsSuccess)
        {
            return null;
        }

        var started = await endpoint.Get<ImpersonationService>().StartAsync(cookie, targetUserId, cancellationToken);
        Assert.Equal((authorized.Error, (ClaimsPrincipal?)null), (started.Error, started.Value));
        return authorized.Error;
    }

    private async Task<ClaimsPrincipal> EndAsync(ClaimsPrincipal cookie)
    {
        await using var endpoint = EndpointHost();
        var ended = await endpoint.Get<ImpersonationService>().EndAsync(cookie, TestContext.Current.CancellationToken);
        Assert.True(ended.IsSuccess, ended.Error);
        return ended.Value!;
    }

    private async Task<Staffora.Core.Entities.Platform.ImpersonationAudit> AuditRowAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ImpersonationAudits.AsNoTracking().SingleAsync(audit => audit.Id == id, cancellationToken);
    }

    private static IdentityRevalidatingAuthenticationStateProvider RevalidationOf(ServiceHost circuit) =>
        new(NullLoggerFactory.Instance, circuit.Get<IServiceScopeFactory>(), circuit.Get<IServiceProvider>());

    private static void AssertNoSession(RuntimeExecutionContext runtime) =>
        Assert.Equal((false, (Guid?)null, (Guid?)null), (runtime.IsInitialized, runtime.UserId, runtime.TenantId));

    private sealed class PauseFirstCommit : DbTransactionInterceptor
    {
        private int paused;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref this.paused, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            }

            return result;
        }
    }

    private sealed class NotYetDispatched : IOutgoingMessageDispatcher
    {
        public Task DispatchAsync(IReadOnlyList<OutgoingMessage> messages, Guid historyEntryId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
