using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class IdentitySignInTests : IClassFixture<RealDatabase>
{
    private const string Password = "Password123!";
    private const string FindLease = "System scope opened for Identity: find a user before a tenant is known (sign-in, cookie validation)";

    private readonly RealDatabase database;

    public IdentitySignInTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task Signing_in_finds_users_of_any_tenant_under_a_reasoned_lease_and_saves_its_bookkeeping_on_their_tenants_behalf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var users = host.Get<UserManager<ApplicationUser>>();
        var signIn = host.Get<SignInManager<ApplicationUser>>();

        foreach (var tenant in new[] { scenario.A, scenario.B, platform })
        {
            var user = (await users.FindByIdAsync(tenant.OwnerId.ToString()))!;
            Assert.True((await users.AddPasswordAsync(user, Password)).Succeeded);

            Assert.False((await signIn.PasswordSignInAsync(user.UserName!, "Wrong123!", isPersistent: false, lockoutOnFailure: true)).Succeeded);
            Assert.False((await signIn.PasswordSignInAsync(user.UserName!, "Wrong123!", isPersistent: false, lockoutOnFailure: true)).Succeeded);
            Assert.Equal(2, (await users.FindByNameAsync(user.UserName!))!.AccessFailedCount);
            Assert.True((await signIn.CheckPasswordSignInAsync((await users.FindByEmailAsync(user.Email!))!, Password, lockoutOnFailure: true)).Succeeded);
        }

        Assert.Contains(host.ContextLog, message => message.StartsWith(FindLease, StringComparison.Ordinal));

        Assert.Empty(await users.Users.ToListAsync(cancellationToken));

        await using var system = this.database.CreateSystemContext();
        var owners = await system.Users.AsNoTracking()
            .Where(user => user.Id == scenario.A.OwnerId || user.Id == scenario.B.OwnerId || user.Id == platform.OwnerId)
            .ToListAsync(cancellationToken);
        Assert.Equal(3, owners.Count);
        Assert.All(owners, owner => Assert.Equal((0, SystemAccount.IdFor(owner.TenantId)), (owner.AccessFailedCount, owner.ModifiedById)));
    }

    [Fact]
    public async Task Failed_attempts_from_a_session_of_another_tenant_are_counted_and_lock_the_account_while_a_profile_change_from_it_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using (var setup = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var users = setup.Get<UserManager<ApplicationUser>>();
            var owner = (await users.FindByIdAsync(scenario.B.OwnerId.ToString()))!;
            Assert.True((await users.AddPasswordAsync(owner, Password)).Succeeded);
            Assert.True((await users.SetLockoutEnabledAsync(owner, true)).Succeeded);
        }

        async Task<SignInResult> AttemptAsync(string password)
        {
            await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.Configure<IdentityOptions>(options => options.Lockout.MaxFailedAccessAttempts = 3));
            var user = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(scenario.B.OwnerId.ToString()))!;
            return await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        }

        Assert.False((await AttemptAsync("Wrong123!")).Succeeded);
        Assert.False((await AttemptAsync("Wrong123!")).Succeeded);
        var counted = await StoredAsync(scenario.B.OwnerId, cancellationToken);
        Assert.Equal((2, (DateTimeOffset?)null, (Guid?)SystemAccount.IdFor(scenario.B.TenantId)), (counted.AccessFailedCount, counted.LockoutEnd, counted.ModifiedById));

        Assert.True((await AttemptAsync("Wrong123!")).IsLockedOut);
        Assert.True((await AttemptAsync(Password)).IsLockedOut);
        var locked = await StoredAsync(scenario.B.OwnerId, cancellationToken);
        Assert.NotNull(locked.LockoutEnd);
        Assert.Equal(SystemAccount.IdFor(scenario.B.TenantId), locked.ModifiedById);

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var users = host.Get<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(scenario.B.OwnerId.ToString()))!;
            var changed = await users.SetPhoneNumberAsync(user, "555-0100");
            Assert.Equal([nameof(ApplicationUserStore.OtherTenantRefused)], changed.Errors.Select(error => error.Code));
        }

        var after = await StoredAsync(scenario.B.OwnerId, cancellationToken);
        Assert.Equal((null, locked.SecurityStamp, locked.LockoutEnd), (after.PhoneNumber, after.SecurityStamp, after.LockoutEnd));
    }

    [Fact]
    public async Task Wrong_passwords_tried_at_once_are_each_counted_and_lock_the_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var member = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Guessed", cancellationToken);
        await using (var setup = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var users = setup.Get<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(member.ToString()))!;
            Assert.True((await users.AddPasswordAsync(user, Password)).Succeeded);
            Assert.True((await users.SetLockoutEnabledAsync(user, true)).Succeeded);
        }

        async Task<SignInResult[]> AtOnceAsync(int attempts)
        {
            var hosts = Enumerable.Range(0, attempts).Select(_ => ServiceHost.Create(this.database, new RuntimeExecutionContext())).ToList();
            try
            {
                var copies = new List<ApplicationUser>();
                foreach (var host in hosts)
                {
                    copies.Add((await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(member.ToString()))!);
                }

                return await Task.WhenAll(hosts.Select((host, index) => Task.Run(() => host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(copies[index], "Wrong-" + index, lockoutOnFailure: true), cancellationToken)));
            }
            finally
            {
                foreach (var host in hosts)
                {
                    await host.DisposeAsync();
                }
            }
        }

        var below = await AtOnceAsync(4);
        Assert.All(below, result => Assert.False(result.Succeeded || result.IsLockedOut));
        var counted = await StoredAsync(member, cancellationToken);
        Assert.Equal((4, (DateTimeOffset?)null, (Guid?)SystemAccount.IdFor(scenario.A.TenantId)), (counted.AccessFailedCount, counted.LockoutEnd, counted.ModifiedById));

        var many = await AtOnceAsync(20);
        Assert.Contains(many, result => result.IsLockedOut);
        Assert.True((await StoredAsync(member, cancellationToken)).LockoutEnd > DateTimeOffset.UtcNow);
        await using var later = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var found = (await later.Get<UserManager<ApplicationUser>>().FindByIdAsync(member.ToString()))!;
        Assert.True((await later.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(found, Password, lockoutOnFailure: true)).IsLockedOut);
    }

    private async Task<ApplicationUser> StoredAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
    }
}
