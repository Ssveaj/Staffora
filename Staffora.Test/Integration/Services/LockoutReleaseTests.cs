using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class LockoutReleaseTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public LockoutReleaseTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Unlocking_a_colleague_requires_UsersEdit_and_lifts_the_lockout_and_the_failed_count()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await LockAsync(scenario.A.OwnerId, cancellationToken);
        var passwordBefore = (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).PasswordHash;

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, RolePermission.UsersView);
        await using (var refusedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague)))
        {
            var refused = await refusedHost.Get<UserService>().StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.UnlockStep);
            Assert.Equal("You do not have permission to edit users.", refused.Error);
        }

        Assert.True(await IsLockedOutAsync(scenario.A.OwnerId, cancellationToken));

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, RolePermission.UsersEdit);
        await using var allowedHost = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var users = allowedHost.Get<UserService>();

        var unlocked = await users.StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.UnlockStep);

        Assert.True(unlocked.IsSuccess, unlocked.Error);
        Assert.False(await IsLockedOutAsync(scenario.A.OwnerId, cancellationToken));

        Assert.Contains("locked out", (await users.StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.UnlockStep)).Error, StringComparison.Ordinal);
        var stored = await StoredUserAsync(scenario.A.OwnerId, cancellationToken);
        Assert.Equal((null, 0, passwordBefore), (stored.LockoutEnd, stored.AccessFailedCount, stored.PasswordHash));
    }

    [Fact]
    public async Task A_new_password_set_by_an_administrator_lifts_the_lockout_too()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await LockAsync(scenario.A.OwnerId, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var reset = await host.Get<UserService>().StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.NewPasswordStep);

        Assert.True(reset.IsSuccess, reset.Error);
        Assert.False(await IsLockedOutAsync(scenario.A.OwnerId, cancellationToken));
        Assert.Equal(0, (await StoredUserAsync(scenario.A.OwnerId, cancellationToken)).AccessFailedCount);
    }

    [Fact]
    public async Task An_administrator_of_another_tenant_cannot_unlock_a_user_or_learn_of_the_lockout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await LockAsync(scenario.A.OwnerId, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.B.Runtime());
        var users = host.Get<UserService>();

        Assert.Equal("User not found.", (await users.StepUserAsync(scenario.A.OwnerId, ApplicationUserWorkflowAttribute.UnlockStep)).Error);
        Assert.True(await IsLockedOutAsync(scenario.A.OwnerId, cancellationToken));
    }

    [Fact]
    public async Task The_platform_console_unlocks_a_user_of_any_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await LockAsync(scenario.B.OwnerId, cancellationToken);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());

        var firstName = (await StoredUserAsync(scenario.B.OwnerId, cancellationToken)).FirstName;
        Assert.Equal(TransitionRefusals.PendingEdits, (await host.Get<UserService>().SaveUserAsync(scenario.B.OwnerId, user => user.FirstName = "Renamed", ApplicationUserWorkflowAttribute.UnlockStep)).Error);
        var updater = (await host.Get<UserService>().LoadApplicationUserUpdaterAsync(scenario.B.OwnerId, cancellationToken)).Value!;
        updater.Entity.FirstName = "Renamed";
        var past = await host.Get<Staffora.Services.Workflow.WorkflowService>().SaveAndTransitionAsync<UserState, ApplicationUser>(updater, ApplicationUserWorkflowAttribute.UnlockStep, Staffora.Services.Services.ApplicationUserWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Equal(TransitionRefusals.PendingEdits, past.Error);
        Assert.Equal(firstName, (await StoredUserAsync(scenario.B.OwnerId, cancellationToken)).FirstName);
        Assert.True(await IsLockedOutAsync(scenario.B.OwnerId, cancellationToken));

        var unlocked = await host.Get<UserService>().StepUserAsync(scenario.B.OwnerId, ApplicationUserWorkflowAttribute.UnlockStep);

        Assert.True(unlocked.IsSuccess, unlocked.Error);
        Assert.False(await IsLockedOutAsync(scenario.B.OwnerId, cancellationToken));

        Assert.Equal(scenario.B.SystemAccountId, (await StoredUserAsync(scenario.B.OwnerId, cancellationToken)).ModifiedById);
    }

    [Fact]
    public async Task The_platform_console_corrects_a_users_name_and_address_and_gives_a_new_password_and_nothing_else()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var staff = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Casey", cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var users = host.Get<UserService>();

        var address = $"casey.{Guid.NewGuid():N}@company-b.example.com";
        var saved = await users.SaveUserAsync(staff, user =>
        {
            user.FirstName = "Cassie";
            user.Email = address;
            user.UserName = address;
        });
        Assert.True(saved.IsSuccess, saved.Error);
        var stored = await StoredUserAsync(staff, cancellationToken);
        Assert.Equal(("Cassie", address, address), (stored.FirstName, stored.Email, stored.UserName));
        Assert.Equal(scenario.B.SystemAccountId, stored.ModifiedById);

        Assert.Equal(UserService.ConsoleEditsIdentityOnly, (await users.SaveUserAsync(staff, user =>
        {
            user.FirstName = "Renamed";
            user.PhoneNumber = "+46 70 000 00 00";
        })).Error);
        Assert.Equal(("Cassie", (string?)null), ((await StoredUserAsync(staff, cancellationToken)).FirstName, (await StoredUserAsync(staff, cancellationToken)).PhoneNumber));

        var stampBefore = (await StoredUserAsync(staff, cancellationToken)).SecurityStamp;
        var reset = await users.StepUserAsync(staff, ApplicationUserWorkflowAttribute.NewPasswordStep);
        Assert.True(reset.IsSuccess, reset.Error);
        Assert.StartsWith("The new password is ", reset.Message, StringComparison.Ordinal);
        Assert.NotEqual(stampBefore, (await StoredUserAsync(staff, cancellationToken)).SecurityStamp);
        Assert.False((await users.StepUserAsync(staff, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);
        Assert.Equal(UserState.Active, (await StoredUserAsync(staff, cancellationToken)).State);
    }

    [Fact]
    public async Task The_unlock_command_lifts_a_platform_owners_lockout_by_address_and_refuses_an_address_nobody_holds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await LockAsync(platform.OwnerId, cancellationToken);
        var owner = await StoredUserAsync(platform.OwnerId, cancellationToken);

        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services => services.AddScoped<AccountUnlockCommand>());
        var command = host.Get<AccountUnlockCommand>();

        var unknown = await command.UnlockAsync($"nobody.{Guid.NewGuid():N}@example.com");
        Assert.False(unknown.IsSuccess);
        Assert.False((await command.UnlockAsync("not an address")).IsSuccess);
        Assert.True(await IsLockedOutAsync(platform.OwnerId, cancellationToken));

        var unlocked = await command.UnlockAsync(owner.Email!);

        Assert.True(unlocked.IsSuccess, unlocked.Error);
        var stored = await StoredUserAsync(platform.OwnerId, cancellationToken);
        Assert.Equal((null, 0, owner.PasswordHash), (stored.LockoutEnd, stored.AccessFailedCount, stored.PasswordHash));
        Assert.Equal(SystemAccount.IdFor(platform.TenantId), stored.ModifiedById);
    }

    [Fact]
    public async Task An_edit_that_reaches_the_unlock_while_it_waits_is_refused_and_nothing_is_stored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        Guid badge;
        await using (var system = this.database.CreateSystemContext())
        {
            var field = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = "Badge", ValueType = AttributeValueType.Text, TenantId = scenario.B.TenantId, CreatedById = scenario.B.OwnerId, Options = [] };
            system.Add(field);
            await system.SaveChangesAsync(cancellationToken);
            badge = field.Id;
        }

        await LockAsync(scenario.B.OwnerId, cancellationToken);
        DetachedEntityUpdater<ApplicationUser>? updater = null;
        var slipIn = new EditWhilePreparing(() => updater!.Entity.Attributes.Add(new ApplicationUserAttribute { EntityAttributeDefinitionId = badge, StringValue = "slipped-in" }));
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), services => services.AddScoped<IStepPreparation>(_ => slipIn));
        var users = host.Get<UserService>();
        updater = (await users.LoadApplicationUserUpdaterAsync(scenario.B.OwnerId, cancellationToken)).Value!;
        Assert.False(updater.HasChanges());

        var unlocked = await users.UpdateApplicationUserAndTransitionAsync(updater, ApplicationUserWorkflowAttribute.UnlockStep, null, cancellationToken);

        Assert.True(slipIn.Ran);
        Assert.Equal(TransitionRefusals.PendingEdits, unlocked.Error);
        await using var check = this.database.CreateSystemContext();
        Assert.Empty(await check.ApplicationUserAttributes.AsNoTracking().Where(value => value.EntityId == scenario.B.OwnerId).ToListAsync(cancellationToken));
        Assert.True(await IsLockedOutAsync(scenario.B.OwnerId, cancellationToken));
    }

    [Fact]
    public async Task A_user_whose_stored_address_was_normalized_otherwise_is_unlocked_with_no_edits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var target = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Target", cancellationToken);
        await LockAsync(target, cancellationToken);
        string? normalizedBefore;
        await using (var context = this.database.CreateSystemContext())
        {
            var user = await context.Users.SingleAsync(row => row.Id == target, cancellationToken);
            user.NormalizedEmail = user.Email;
            normalizedBefore = user.NormalizedEmail;
            context.NameEditor(user, SystemAccount.IdFor(user.TenantId));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        var unlocked = await host.Get<UserService>().StepUserAsync(target, ApplicationUserWorkflowAttribute.UnlockStep);

        Assert.True(unlocked.IsSuccess, unlocked.Error);
        var stored = await StoredUserAsync(target, cancellationToken);
        Assert.False(await IsLockedOutAsync(target, cancellationToken));
        Assert.Equal(normalizedBefore, stored.NormalizedEmail);
    }

    private sealed class EditWhilePreparing : IStepPreparation
    {
        private readonly Action edit;

        public EditWhilePreparing(Action edit)
        {
            this.edit = edit;
        }

        public bool Ran { get; private set; }

        public Type EntityType => typeof(ApplicationUser);

        public Task PrepareAsync(Staffora.Data.ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
        {
            if (!Ran)
            {
                Ran = true;
                this.edit();
            }

            return Task.CompletedTask;
        }
    }

    private async Task LockAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.SingleAsync(row => row.Id == userId, cancellationToken);
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
        user.AccessFailedCount = 3;
        context.NameEditor(user, SystemAccount.IdFor(user.TenantId));
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsLockedOutAsync(Guid userId, CancellationToken cancellationToken) =>
        (await StoredUserAsync(userId, cancellationToken)).LockoutEnd > DateTimeOffset.UtcNow;

    private async Task<ApplicationUser> StoredUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
    }
}
