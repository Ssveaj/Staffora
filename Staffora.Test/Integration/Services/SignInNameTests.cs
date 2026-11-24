using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class SignInNameTests : IClassFixture<RealDatabase>
{
    private const string Password = "Password123!";

    private readonly RealDatabase database;

    public SignInNameTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Creating_or_renaming_a_user_to_another_tenants_or_the_platform_owners_address_is_refused_and_the_holder_still_signs_in_and_reaches_the_console()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersCreate, RolePermission.UsersEdit, RolePermission.UsersView);
        var ownerAddress = await AddressOfAsync(platform.OwnerId, cancellationToken);
        var addressInB = await AddressOfAsync(scenario.B.OwnerId, cancellationToken);

        Guid colleague;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var users = host.Get<UserService>();
            foreach (var taken in new[] { ownerAddress, addressInB })
            {
                var asAddress = await users.CreateUserAsync(NewUser($"new.{Guid.NewGuid():N}@company-a.example", taken), scenario.A.TenantId, Password);
                var asBoth = await users.CreateUserAsync(NewUser(taken, taken), scenario.A.TenantId, Password);
                Assert.Equal((false, UserService.SignInNameUnavailable), (asAddress.IsSuccess, asAddress.Error));
                Assert.Equal((false, UserService.SignInNameUnavailable), (asBoth.IsSuccess, asBoth.Error));
            }

            var address = $"colleague.{Guid.NewGuid():N}@company-a.example";
            var created = await users.CreateUserAsync(NewUser(address, address), scenario.A.TenantId, Password);
            Assert.True(created.IsSuccess, created.Error);
            colleague = created.Value!.Id;

            foreach (var taken in new[] { ownerAddress, addressInB })
            {
                var renamed = await users.SaveUserAsync(colleague, user => user.Email = taken);
                Assert.Equal((false, UserService.SignInNameUnavailable), (renamed.IsSuccess, renamed.Error));
            }
        }

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.Equal(1, await check.Users.CountAsync(user => user.NormalizedEmail == ownerAddress.ToUpperInvariant(), cancellationToken));
            Assert.Equal(1, await check.Users.CountAsync(user => user.NormalizedEmail == addressInB.ToUpperInvariant(), cancellationToken));
            Assert.NotEqual(ownerAddress, (await check.Users.SingleAsync(user => user.Id == colleague, cancellationToken)).Email);
        }

        await SetPasswordAsync(platform.OwnerId);
        await using var signIn = SignInHost();
        var userManager = signIn.Get<UserManager<ApplicationUser>>();
        var signInManager = (ApplicationSignInManager)signIn.Get<SignInManager<ApplicationUser>>();

        Assert.Equal(platform.OwnerId, (await userManager.FindByEmailAsync(ownerAddress))?.Id);
        Assert.True((await signInManager.PasswordSignInAsync(ownerAddress, Password, isPersistent: false, lockoutOnFailure: true)).Succeeded);
        Assert.Equal(platform.OwnerId, signInManager.SignedInUser?.Id);
        Assert.Contains(AppRoles.Admin, await userManager.GetRolesAsync(signInManager.SignedInUser!));
    }

    [Fact]
    public async Task A_name_or_address_taken_in_another_tenant_gets_the_same_answer_as_one_taken_in_the_callers_own_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersCreate, RolePermission.UsersEdit, RolePermission.UsersView);
        var colleagueOfA = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var takenInA = await AddressOfAsync(colleagueOfA, cancellationToken);
        var takenInB = await AddressOfAsync(scenario.B.OwnerId, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var users = host.Get<UserService>();
        var fresh = $"fresh.{Guid.NewGuid():N}@company-a.example";

        var answers = new List<string?>();
        foreach (var taken in new[] { takenInA, takenInB })
        {
            answers.Add((await users.CreateUserAsync(NewUser(taken, $"other.{Guid.NewGuid():N}@company-a.example"), scenario.A.TenantId, Password)).Error);
            answers.Add((await users.CreateUserAsync(NewUser($"other.{Guid.NewGuid():N}@company-a.example", taken), scenario.A.TenantId, Password)).Error);
            answers.Add((await users.SaveUserAsync(scenario.A.OwnerId, user => { user.UserName = taken; user.Email = fresh; })).Error);
        }

        Assert.All(answers, answer => Assert.Equal(UserService.SignInNameUnavailable, answer));
    }

    [Fact]
    public async Task An_address_two_users_share_despite_the_index_finds_nobody_instead_of_failing_and_its_holder_still_signs_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var shared = await AddressOfAsync(scenario.A.OwnerId, cancellationToken);
        await SetPasswordAsync(scenario.A.OwnerId);

        await using (var context = this.database.CreateSystemContext())
        {
            context.Users.Add(Squatter(scenario.B, shared));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        }

        Guid squatter;
        await using (var context = this.database.CreateSystemContext())
        {
            await context.Database.ExecuteSqlRawAsync("ALTER INDEX [EmailIndex] ON [AspNetUsers] DISABLE", cancellationToken);
            var user = Squatter(scenario.B, shared);
            context.Users.Add(user);
            await context.SaveChangesAsync(cancellationToken);
            squatter = user.Id;
        }

        try
        {
            await using var signIn = SignInHost();
            var signInManager = (ApplicationSignInManager)signIn.Get<SignInManager<ApplicationUser>>();

            Assert.Null(await signIn.Get<UserManager<ApplicationUser>>().FindByEmailAsync(shared));
            Assert.True((await signInManager.PasswordSignInAsync(shared, Password, isPersistent: false, lockoutOnFailure: true)).Succeeded);
            Assert.Equal(scenario.A.OwnerId, signInManager.SignedInUser?.Id);
        }
        finally
        {
            await UserEdits.DeleteInTableAsync(this.database, squatter, cancellationToken);
            await using var context = this.database.CreateSystemContext();
            await context.Database.ExecuteSqlRawAsync("ALTER INDEX [EmailIndex] ON [AspNetUsers] REBUILD", cancellationToken);
        }
    }

    [Fact]
    public async Task User_names_and_addresses_are_trimmed_on_create_and_on_update()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit);
        var created = $"padded.{Guid.NewGuid():N}@company-a.example";
        var renamed = $"renamed.{Guid.NewGuid():N}@company-a.example";
        Guid userId;

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var users = host.Get<UserService>();
            var result = await users.CreateUserAsync(NewUser($"  {created} ", $" {created}  "), scenario.A.TenantId, Password);
            Assert.True(result.IsSuccess, result.Error);
            userId = result.Value!.Id;
            await AssertStoredAsync(userId, created, cancellationToken);

            var updated = await users.SaveUserAsync(userId, user =>
            {
                user.UserName = $" {renamed}\t";
                user.Email = $"\t{renamed} ";
            });
            Assert.True(updated.IsSuccess, updated.Error);
        }

        await AssertStoredAsync(userId, renamed, cancellationToken);

        await using var signIn = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        Assert.Equal(userId, (await signIn.Get<UserManager<ApplicationUser>>().FindByEmailAsync(renamed))?.Id);
    }

    private async Task AssertStoredAsync(Guid userId, string expected, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var stored = await context.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
        Assert.Equal((expected, expected, expected.ToUpperInvariant(), expected.ToUpperInvariant()), (stored.UserName, stored.Email, stored.NormalizedUserName, stored.NormalizedEmail));
    }

    private ServiceHost SignInHost()
    {
        var accessor = new HttpContextAccessor();
        var host = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<IHttpContextAccessor>(accessor);
            services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        });
        accessor.HttpContext = new DefaultHttpContext { RequestServices = host.Get<IServiceProvider>() };
        return host;
    }

    private async Task SetPasswordAsync(Guid userId)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var users = host.Get<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId.ToString()))!;
        Assert.True((await users.AddPasswordAsync(user, Password)).Succeeded);
    }

    private async Task<string> AddressOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return (await context.Users.SingleAsync(user => user.Id == userId, cancellationToken)).Email!;
    }

    private static ApplicationUser Squatter(ScenarioTenant tenant, string email)
    {
        var userName = $"squatter.{Guid.NewGuid():N}@company-b.example";
        return new ApplicationUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = "Squatter",
            LastName = tenant.Name,
            TenantId = tenant.TenantId,
            CreatedById = tenant.SystemAccountId,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
    }

    private static ApplicationUser NewUser(string userName, string email) =>
        new() { UserName = userName, Email = email, FirstName = "New", LastName = "User" };
}
