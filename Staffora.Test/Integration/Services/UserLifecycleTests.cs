using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class UserLifecycleTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public UserLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_user_is_created_deactivated_and_reactivated_through_the_user_workflows_transitions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();

        var email = $"created.{Guid.NewGuid():N}@company-a.example";
        var draft = (await users.NewApplicationUserAsync(cancellationToken)).Value!;
        Assert.Equal((UserState.Create, scenario.A.TenantId), (draft.State, draft.TenantId));
        draft.FirstName = "Created";
        draft.LastName = "User";
        draft.Email = email;
        draft.UserName = email;
        var created = await users.CreateApplicationUserAsync(draft, ApplicationUserWorkflowAttribute.CreateStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        var id = created.Value;
        var password = InitialPasswordIn(created.Message);
        var stored = await StoredAsync(id, cancellationToken);
        Assert.Equal((UserState.Active, false, scenario.A.TenantId, scenario.A.OwnerId), (stored.State, stored.IsInTransition, stored.TenantId, stored.CreatedById));
        Assert.Equal(email.ToUpperInvariant(), stored.NormalizedEmail);
        Assert.True(await SignsInAsync(id, password));

        var stampBefore = stored.SecurityStamp;
        var deactivated = await users.StepUserAsync(id, ApplicationUserWorkflowAttribute.DeactivateStep);
        Assert.True(deactivated.IsSuccess, deactivated.Error);
        stored = await StoredAsync(id, cancellationToken);
        Assert.Equal(UserState.Inactive, stored.State);
        Assert.NotEqual(stampBefore, stored.SecurityStamp);
        Assert.False(await SignsInAsync(id, password));

        var locked = await users.SaveUserAsync(id, user => user.FirstName = "Changed", ApplicationUserWorkflowAttribute.ReactivateStep);
        Assert.False(locked.IsSuccess);
        Assert.Equal("Created", (await StoredAsync(id, cancellationToken)).FirstName);

        var reactivated = await users.StepUserAsync(id, ApplicationUserWorkflowAttribute.ReactivateStep);
        Assert.True(reactivated.IsSuccess, reactivated.Error);
        Assert.Equal(UserState.Active, (await StoredAsync(id, cancellationToken)).State);
        Assert.True(await SignsInAsync(id, password));

        var history = await HistoryAsync(id, cancellationToken);
        Assert.Equal(
            [
                (ApplicationUserWorkflowAttribute.CreateStep, (int)UserState.Create, (int)UserState.Active),
                (ApplicationUserWorkflowAttribute.DeactivateStep, (int)UserState.Active, (int)UserState.Inactive),
                (ApplicationUserWorkflowAttribute.ReactivateStep, (int)UserState.Inactive, (int)UserState.Active),
            ],
            history.Select(entry => (entry.TransitionKey, entry.FromState, entry.ToState)));
        Assert.All(history, entry => Assert.Equal(scenario.A.OwnerId, entry.ActorId));
    }

    [Fact]
    public async Task Two_administrators_editing_different_fields_of_one_user_both_keep_their_edits_whatever_Identity_wrote_between_them()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var second = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Second", cancellationToken);
        var target = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Target", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, second, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await using var firstHost = ServiceHost.Create(this.database, scenario.A.Runtime());
        await using var secondHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(second));

        var first = (await firstHost.Get<UserService>().LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;
        var other = (await secondHost.Get<UserService>().LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;
        var stampLoaded = (await StoredAsync(target, cancellationToken)).ConcurrencyStamp;

        first.Entity.LastName = "Renamed";
        var saved = await firstHost.Get<UserService>().UpdateApplicationUserAndTransitionAsync(first, ApplicationUserWorkflowAttribute.SaveStep, null, cancellationToken);
        Assert.True(saved.IsSuccess, saved.Error);

        await using (var signIn = ServiceHost.Create(this.database, new Staffora.Core.Execution.RuntimeExecutionContext()))
        {
            var identity = signIn.Get<UserManager<ApplicationUser>>();
            Assert.True((await identity.AccessFailedAsync((await identity.FindByIdAsync(target.ToString()))!)).Succeeded);
        }

        other.Entity.PhoneNumber = "+46 70 000 00 00";
        var alsoSaved = await secondHost.Get<UserService>().UpdateApplicationUserAndTransitionAsync(other, ApplicationUserWorkflowAttribute.SaveStep, null, cancellationToken);
        Assert.True(alsoSaved.IsSuccess, alsoSaved.Error);
        var stored = await StoredAsync(target, cancellationToken);
        Assert.Equal(("Renamed", "+46 70 000 00 00", 1), (stored.LastName, stored.PhoneNumber, stored.AccessFailedCount));

        Assert.NotEqual(stampLoaded, stored.ConcurrencyStamp);

        var firstAgain = (await firstHost.Get<UserService>().LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;
        var otherAgain = (await secondHost.Get<UserService>().LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;
        firstAgain.Entity.FirstName = "First";
        otherAgain.Entity.FirstName = "Last";
        Assert.True((await firstHost.Get<UserService>().UpdateApplicationUserAndTransitionAsync(firstAgain, ApplicationUserWorkflowAttribute.SaveStep, null, cancellationToken)).IsSuccess);
        Assert.True((await secondHost.Get<UserService>().UpdateApplicationUserAndTransitionAsync(otherAgain, ApplicationUserWorkflowAttribute.SaveStep, null, cancellationToken)).IsSuccess);
        Assert.Equal("Last", (await StoredAsync(target, cancellationToken)).FirstName);

        var stale = (await secondHost.Get<UserService>().LoadApplicationUserUpdaterAsync(target, cancellationToken)).Value!;
        Assert.True((await firstHost.Get<UserService>().StepUserAsync(target, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);
        stale.Entity.PhoneNumber = "+46 70 111 11 11";
        Assert.False((await secondHost.Get<UserService>().UpdateApplicationUserAndTransitionAsync(stale, ApplicationUserWorkflowAttribute.SaveStep, null, cancellationToken)).IsSuccess);
        Assert.Equal("+46 70 000 00 00", (await StoredAsync(target, cancellationToken)).PhoneNumber);
    }

    [Fact]
    public async Task The_dialogs_updater_never_writes_Identitys_own_columns_and_keeps_the_normalized_name_with_the_name()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var target = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Target", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        var before = await StoredAsync(target, cancellationToken);

        var renamed = $"renamed.{Guid.NewGuid():N}@company-a.example";
        var saved = await users.SaveUserAsync(target, user =>
        {
            user.UserName = renamed;
            user.NormalizedUserName = "SOMEONE.ELSE";
            user.PasswordHash = "crafted";
            user.SecurityStamp = "crafted";
            user.LockoutEnd = DateTimeOffset.UtcNow.AddYears(1);
            user.AccessFailedCount = 4;
            user.EmailConfirmed = !before.EmailConfirmed;
            user.TwoFactorEnabled = !before.TwoFactorEnabled;
            user.LockoutEnabled = !before.LockoutEnabled;
        });
        Assert.True(saved.IsSuccess, saved.Error);

        var stored = await StoredAsync(target, cancellationToken);
        Assert.Equal((renamed, renamed.ToUpperInvariant()), (stored.UserName, stored.NormalizedUserName));
        Assert.Equal((before.PasswordHash, before.SecurityStamp, before.LockoutEnd, before.AccessFailedCount, before.EmailConfirmed), (stored.PasswordHash, stored.SecurityStamp, stored.LockoutEnd, stored.AccessFailedCount, stored.EmailConfirmed));
        Assert.Equal((before.TwoFactorEnabled, before.LockoutEnabled), (stored.TwoFactorEnabled, stored.LockoutEnabled));
    }

    [Fact]
    public async Task Nobody_deactivates_their_own_account_and_a_system_account_is_never_moved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());

        var own = await host.Get<UserService>().StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.DeactivateStep);
        Assert.Equal(ApplicationUserWorkflowAttribute.NotSelf, own.Error);
        Assert.Equal(UserState.Active, (await StoredAsync(scenario.A.OwnerId, cancellationToken)).State);

        Assert.Equal(UserService.UserNotFound, (await host.Get<UserService>().StepUserAsync(scenario.A.SystemAccountId, ApplicationUserWorkflowAttribute.DeactivateStep)).Error);
        var past = await host.Get<WorkflowService>().TransitionEntityAsync<UserState, ApplicationUser>(scenario.A.SystemAccountId, ApplicationUserWorkflowAttribute.DeactivateStep, ApplicationUserWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.False(past.IsSuccess);
        Assert.Contains("system account", past.Error, StringComparison.Ordinal);
        Assert.Equal(UserState.Active, (await StoredAsync(scenario.A.SystemAccountId, cancellationToken)).State);
    }

    [Fact]
    public async Task The_platform_console_corrects_a_tenants_user_but_neither_creates_one_nor_changes_their_lifecycle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var users = console.Get<UserService>();

        Assert.True((await users.LoadApplicationUserUpdaterAsync(scenario.A.OwnerId, cancellationToken)).IsSuccess);
        Assert.Equal((false, true, false), (await users.GetApplicationUserEditorOptionsAsync(scenario.A.OwnerId, cancellationToken)) is { Value: { } options } ? (options.FieldsLocked, options.OnConsole, options.CanManageRoles) : default);

        Assert.Equal(UserService.CreateInOwnTenantOnly, (await users.NewApplicationUserAsync(cancellationToken)).Error);
        var email = $"console.{Guid.NewGuid():N}@company-a.example";
        Assert.Equal(UserService.CreateInOwnTenantOnly, (await users.CreateUserAsync(new ApplicationUser { FirstName = "Console", LastName = "Made", Email = email, UserName = email }, scenario.A.TenantId, "Password123!")).Error);
        Assert.Equal(UserService.ConsoleEditsIdentityOnly, (await users.SaveUserAsync(scenario.A.OwnerId, user => { user.FirstName = "Console"; user.PhoneNumber = "+46 8 000 00"; })).Error);
        Assert.False((await users.StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);

        var stored = await StoredAsync(scenario.A.OwnerId, cancellationToken);
        Assert.Equal(UserState.Active, stored.State);
        Assert.NotEqual("Console", stored.FirstName);
        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.Users.AnyAsync(user => user.Email == email, cancellationToken));
    }

    private static string InitialPasswordIn(string? message)
    {
        Assert.NotNull(message);
        var start = message.IndexOf("password is ", StringComparison.Ordinal) + "password is ".Length;
        return message[start..message.IndexOf(' ', start)];
    }

    private async Task<bool> SignsInAsync(Guid userId, string password)
    {
        await using var signIn = ServiceHost.Create(this.database, new Staffora.Core.Execution.RuntimeExecutionContext());
        var manager = signIn.Get<SignInManager<ApplicationUser>>();
        var user = await signIn.Get<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString());
        return user is not null && (await manager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false)).Succeeded;
    }

    private async Task<ApplicationUser> StoredAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
    }

    private async Task<List<StateHistoryEntry>> HistoryAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == userId).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(cancellationToken);
    }
}
