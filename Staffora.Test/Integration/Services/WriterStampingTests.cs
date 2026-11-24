using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class WriterStampingTests : IClassFixture<RealDatabase>
{
    private const string Password = "Password123!";

    private readonly RealDatabase database;

    public WriterStampingTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_platform_owner_renaming_themselves_on_the_console_is_stamped_with_themselves_not_the_platforms_system_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await SetPasswordBeforeAnySessionAsync(platform.OwnerId);
        Assert.Equal(platform.SystemAccountId, (await LoadUserAsync(platform.OwnerId, cancellationToken)).ModifiedById);

        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var users = host.Get<UserService>();
            var saved = await users.SaveUserAsync(platform.OwnerId, owner => owner.FirstName = "Renamed");
            Assert.True(saved.IsSuccess, saved.Error);
            Assert.Contains(host.ContextLog, message => message.Contains("System scope opened for Platform console: workflow steps of a ApplicationUser in any tenant", StringComparison.Ordinal));
        }

        var stored = await LoadUserAsync(platform.OwnerId, cancellationToken);
        Assert.Equal(("Renamed", (Guid?)platform.OwnerId), (stored.FirstName, stored.ModifiedById));
    }

    [Fact]
    public async Task An_administrators_new_password_for_a_colleague_is_stamped_with_the_administrator_and_the_platform_consoles_with_the_tenants_system_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        await SetPasswordBeforeAnySessionAsync(colleague);

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var reset = await host.Get<UserService>().StepUserAsync(colleague, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.True(reset.IsSuccess, reset.Error);
        }

        Assert.Equal(scenario.A.OwnerId, (await LoadUserAsync(colleague, cancellationToken)).ModifiedById);

        var before = await LoadUserAsync(colleague, cancellationToken);
        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var reset = await host.Get<UserService>().StepUserAsync(colleague, ApplicationUserWorkflowAttribute.NewPasswordStep);
            Assert.True(reset.IsSuccess, reset.Error);
        }

        var after = await LoadUserAsync(colleague, cancellationToken);
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
        Assert.Equal(scenario.A.SystemAccountId, after.ModifiedById);
    }

    [Fact]
    public async Task In_system_scope_an_editor_left_as_the_row_held_it_is_replaced_with_the_runtimes_user_so_a_user_of_another_tenant_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        Guid id;

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = new TestParent { Name = "Edited on B's behalf", TenantId = scenario.B.TenantId, CreatedById = scenario.B.SystemAccountId };
            system.TestParents.Add(parent);
            await system.SaveChangesAsync(cancellationToken);
            parent.Notes = "On B's behalf";
            system.NameEditor(parent, scenario.B.SystemAccountId);
            await system.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        using (context.BeginSystemScope("test: edit a row of B as A's user"))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Notes = "Edited by A's user";
            var refusal = await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Contains("not a user of the row's tenant", refusal.Message, StringComparison.Ordinal);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = await system.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Notes = "Named again";
            system.NameEditor(parent, scenario.B.SystemAccountId);
            await system.SaveChangesAsync(cancellationToken);
            parent.Notes = "Not named again";
            await Assert.ThrowsAsync<InvalidOperationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
        Assert.Equal(("Named again", (Guid?)scenario.B.SystemAccountId), (stored.Notes, stored.ModifiedById));
    }

    [Fact]
    public async Task Identitys_store_refuses_a_session_in_one_tenant_creating_updating_or_deleting_a_user_of_another_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Colleague", cancellationToken);
        var strangerEmail = $"stranger.{Guid.NewGuid():N}@company-b.example";
        var impersonatingA = platform.PlatformOwnerRuntime();
        impersonatingA.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);

        foreach (var runtime in new IRuntimeExecutionContext[] { scenario.A.Runtime(), impersonatingA })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            var users = host.Get<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(colleague.ToString()))!;
            user.FirstName = "Renamed from A";
            Assert.Equal(ApplicationUserStore.OtherTenantRefused, Assert.Single((await users.UpdateAsync(user)).Errors).Description);
            Assert.Equal(ApplicationUserStore.OtherTenantRefused, Assert.Single((await users.DeleteAsync(user)).Errors).Description);

            var stranger = new ApplicationUser { UserName = strangerEmail, Email = strangerEmail, FirstName = "Stranger", LastName = "B", TenantId = scenario.B.TenantId, State = UserState.Active };
            Assert.Equal(ApplicationUserStore.CreateOutsideSetupRefused, Assert.Single((await users.CreateAsync(stranger, Password)).Errors).Description);
        }

        var unchanged = await LoadUserAsync(colleague, cancellationToken);
        Assert.Equal("Colleague", unchanged.FirstName);
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.Users.AnyAsync(user => user.Email == strangerEmail, cancellationToken));
        }

        foreach (var (runtime, name) in new (IRuntimeExecutionContext, string)[] { (new RuntimeExecutionContext(), "Before a session"), (platform.PlatformOwnerRuntime(), "From the console") })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            var users = host.Get<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(colleague.ToString()))!;
            user.FirstName = name;
            var saved = await users.UpdateAsync(user);
            Assert.True(saved.Succeeded, string.Join(" ", saved.Errors.Select(error => error.Description)));
            Assert.Equal((name, (Guid?)scenario.B.SystemAccountId), ((await LoadUserAsync(colleague, cancellationToken)).FirstName, (await LoadUserAsync(colleague, cancellationToken)).ModifiedById));
        }
    }

    private async Task SetPasswordBeforeAnySessionAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var users = host.Get<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId.ToString()))!;
        Assert.True((await users.AddPasswordAsync(user, Password)).Succeeded);
    }

    private async Task<ApplicationUser> LoadUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
    }
}
