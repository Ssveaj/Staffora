using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Blazor.Helper;
using Staffora.Services.Authorization;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
[Collection(ServerProcessCollection.Name)]
public sealed class PlatformOwnerResetCommandTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public PlatformOwnerResetCommandTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_command_gives_a_platform_owner_a_new_password_and_takes_away_every_other_way_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await SetPasswordAsync(platform.OwnerId, "Old-password-1!", cancellationToken);
        await GiveSecondFactorsAndLockAsync(platform.OwnerId, cancellationToken);
        var before = await StoredAsync(platform.OwnerId, cancellationToken);

        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var reset = await host.Get<PlatformOwnerResetCommand>().ResetAsync(before.Email!, cancellationToken);

        Assert.True(reset.IsSuccess, reset.Error);
        Assert.Equal(before.Email, reset.Value!.Email);
        Assert.Matches("^\\S{16}$", reset.Value.InitialPassword);
        Assert.DoesNotContain(reset.Value.InitialPassword, reset.Value.ToString(), StringComparison.Ordinal);

        var stored = await StoredAsync(platform.OwnerId, cancellationToken);
        var hasher = new PasswordHasher<ApplicationUser>();
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash!, reset.Value.InitialPassword));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash!, "Old-password-1!"));
        Assert.NotEqual(before.SecurityStamp, stored.SecurityStamp);
        Assert.Equal(((DateTimeOffset?)null, 0, false), (stored.LockoutEnd, stored.AccessFailedCount, stored.TwoFactorEnabled));
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.UserTokens.AnyAsync(token => token.UserId == platform.OwnerId, cancellationToken));

            using var lease = check.BeginSystemScope("test: read the owner's history");
            var entry = await check.StateHistoryEntries.AsNoTracking().SingleAsync(row => row.EntityId == platform.OwnerId, cancellationToken);
            Assert.Equal(
                (ApplicationUserWorkflowAttribute.NewPasswordStep, platform.SystemAccountId, platform.SystemAccountId, platform.TenantId, (int)UserState.Active, (int)UserState.Active),
                (entry.TransitionKey, entry.ActorId, entry.CreatedById, entry.TenantId, entry.FromState, entry.ToState));
        }

        Assert.Equal(platform.SystemAccountId, stored.ModifiedById);

        Assert.DoesNotContain(host.ContextLog, message => message.Contains(reset.Value.InitialPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_command_refuses_an_address_nobody_holds_a_tenants_user_and_an_Admin_outside_the_platform_tenant_and_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var platformMember = await TwoTenantScenario.AddUserAsync(this.database, platform, "Member", cancellationToken);
        await TwoTenantScenario.GiveAdminRoleAsync(this.database, scenario.B.OwnerId, cancellationToken);
        var tenantUser = await StoredAsync(scenario.A.OwnerId, cancellationToken);
        var adminOfB = await StoredAsync(scenario.B.OwnerId, cancellationToken);
        var member = await StoredAsync(platformMember, cancellationToken);

        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var command = host.Get<PlatformOwnerResetCommand>();
        var nobody = $"nobody.{Guid.NewGuid():N}@example.com";

        Assert.Equal(PlatformOwnerResetCommand.NoAddress, (await command.ResetAsync("not an address", cancellationToken)).Error);
        Assert.Equal(PlatformOwnerResetCommand.NoSuchUser(nobody), (await command.ResetAsync(nobody, cancellationToken)).Error);

        foreach (var user in new[] { tenantUser, adminOfB, member })
        {
            Assert.Equal(PlatformOwnerResetCommand.NotAPlatformOwner(user.Email!), (await command.ResetAsync(user.Email!, cancellationToken)).Error);
            var after = await StoredAsync(user.Id, cancellationToken);
            Assert.Equal((user.PasswordHash, user.SecurityStamp, user.ConcurrencyStamp), (after.PasswordHash, after.SecurityStamp, after.ConcurrencyStamp));
        }
    }

    [Fact]
    public async Task The_reset_ends_the_cookies_the_platform_owner_signed_in_with_before()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var app = AppHost.Start(this.database);
        using var browser = app.CreateBrowser();
        await browser.SignInAsync(platform.OwnerId);
        using (var opened = await browser.Http.GetAsync("/superadmin/overview", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        }

        await using (var host = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var reset = await host.Get<PlatformOwnerResetCommand>().ResetAsync((await StoredAsync(platform.OwnerId, cancellationToken)).Email!, cancellationToken);
            Assert.True(reset.IsSuccess, reset.Error);
        }

        using var refused = await browser.Http.GetAsync("/superadmin/overview", cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("/Account/Login", refused.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_prints_the_new_password_once_and_exits_non_zero_on_a_refusal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var ownerEmail = (await StoredAsync(platform.OwnerId, cancellationToken)).Email!;
        var tenantUserEmail = (await StoredAsync(scenario.A.OwnerId, cancellationToken)).Email!;

        var usage = await RunAsync(cancellationToken, PlatformOwnerResetCommand.Command, "not-an-address");
        Assert.Equal(2, usage.ExitCode);
        Assert.Contains($"Usage: dotnet run --project Staffora.Blazor -- {PlatformOwnerResetCommand.Command} <email>", usage.Error, StringComparison.Ordinal);

        var unknown = await RunAsync(cancellationToken, PlatformOwnerResetCommand.Command, $"nobody.{Guid.NewGuid():N}@example.com");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("No user signs in with", unknown.Error, StringComparison.Ordinal);

        var tenantUser = await RunAsync(cancellationToken, PlatformOwnerResetCommand.Command, tenantUserEmail);
        Assert.Equal(1, tenantUser.ExitCode);
        Assert.Contains(PlatformOwnerResetCommand.NotAPlatformOwner(tenantUserEmail), tenantUser.Error, StringComparison.Ordinal);

        var reset = await RunAsync(cancellationToken, PlatformOwnerResetCommand.Command, ownerEmail);
        Assert.True(reset.ExitCode == 0, reset.Error);
        var line = Assert.Single(reset.Output.Split('\n'), text => text.StartsWith("New password, shown this once: ", StringComparison.Ordinal)).Trim();
        var password = line["New password, shown this once: ".Length..];

        Assert.Equal(1, Occurrences(reset.Output + reset.Error, password));
        var stored = await StoredAsync(platform.OwnerId, cancellationToken);
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<ApplicationUser>().VerifyHashedPassword(stored, stored.PasswordHash!, password));
    }

    [Fact]
    public async Task A_command_whose_database_does_not_answer_exits_with_one_line_and_code_one_and_names_no_address()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var unreachable = new SqlConnectionStringBuilder(this.database.ConnectionString) { InitialCatalog = $"missing_{Guid.NewGuid():N}_test", ConnectTimeout = 5 }.ConnectionString;
        var email = $"owner.{Guid.NewGuid():N}@example.com";

        foreach (var command in new[] { PlatformOwnerResetCommand.Command, AccountUnlockCommand.Command })
        {
            var run = await RunOnAsync(unreachable, cancellationToken, command, email);

            Assert.True(run.ExitCode == 1, $"{command} exited with {run.ExitCode}: {run.Error}");
            var line = Assert.Single(run.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.StartsWith($"{command} failed (", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", run.Output + run.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(email, run.Output + run.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_web_apps_container_holds_none_of_the_server_commands()
    {
        var commands = typeof(PlatformOwnerResetCommand).Assembly.GetTypes().Where(type => type.Name.EndsWith("Command", StringComparison.Ordinal) && type is { IsClass: true, IsAbstract: false }).ToList();
        Assert.Contains(typeof(PlatformOwnerResetCommand), commands);
        Assert.Contains(typeof(AccountUnlockCommand), commands);

        await using var app = AppHost.Start(this.database);
        var registered = app.Services.GetRequiredService<IServiceProviderIsService>();
        Assert.Empty(commands.Where(registered.IsService).Select(type => type.Name));

        var commandPath = new ServiceCollection().AddServerCommands();
        Assert.Equal([typeof(AccountUnlockCommand), typeof(PlatformOwnerResetCommand)], commandPath.Select(service => service.ServiceType).Order(Comparer<Type>.Create((left, right) => string.CompareOrdinal(left.Name, right.Name))));
    }

    private Task<(int ExitCode, string Output, string Error)> RunAsync(CancellationToken cancellationToken, params string[] arguments) =>
        RunOnAsync(this.database.ConnectionString, cancellationToken, arguments);

    private static async Task<(int ExitCode, string Output, string Error)> RunOnAsync(string connectionString, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.Combine(RepositoryRoot(), "Staffora.Blazor"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Staffora.Blazor.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--environment");
        start.ArgumentList.Add("Test");
        start.ArgumentList.Add($"--ConnectionStrings:DefaultConnection={connectionString}");

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Staffora.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Staffora.slnx was not found above the test's folder.");
    }

    private async Task SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.SingleAsync(row => row.Id == userId, cancellationToken);
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
        context.NameEditor(user, SystemAccount.IdFor(user.TenantId));
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task GiveSecondFactorsAndLockAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        context.UserTokens.Add(new IdentityUserToken<Guid> { UserId = userId, LoginProvider = "[AspNetUserStore]", Name = "AuthenticatorKey", Value = "JBSWY3DPEHPK3PXP" });
        context.UserTokens.Add(new IdentityUserToken<Guid> { UserId = userId, LoginProvider = "[AspNetUserStore]", Name = "RecoveryCodes", Value = "AAAA-BBBB;CCCC-DDDD" });
        await context.SaveChangesAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET TwoFactorEnabled = 1, LockoutEnabled = 1, LockoutEnd = {DateTimeOffset.UtcNow.AddMinutes(15)}, AccessFailedCount = 3 WHERE Id = {userId}", cancellationToken);
    }

    private async Task<ApplicationUser> StoredAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServerProcessCollection
{
    public const string Name = "Server process";
}
