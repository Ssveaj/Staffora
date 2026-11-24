using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class UserCreationScanTests
{
    private static readonly Dictionary<string, string> allowList = new()
    {
        ["Staffora.Data/Seed/DatabaseSeeder.cs"] = "the development seeding of the platform's first owner, before any session, names Active itself",
        ["Staffora.Data/Seed/PlatformBootstrap.cs"] = "the one-time bootstrap of the first platform owner, before any session, names Active itself",
    };

    private static readonly Dictionary<string, string> onboardingWriterCallers = new()
    {
        ["Staffora.Services/Services/TenantOnboardingService.cs"] = "tenant onboarding on the platform console, inside the tenant's create, after the platform-reach check",
    };

    private static readonly Dictionary<string, string> userRowAdders = new()
    {
        ["Staffora.Data/TenantOnboardingWriter.cs"] = "a new tenant's first system owner, judged by Identity's rules before, written on the tenant's behalf",
        ["Staffora.Data/SystemAccountService.cs"] = "a tenant's system account, which signs in nowhere and is never impersonated",
    };

    private static readonly string[] productProjects = ["Staffora.Core/", "Staffora.Data/", "Staffora.Services/", "Staffora.Blazor/", "Staffora.E2ESetup/"];

    [Fact]
    public void No_product_file_calls_Identitys_create_but_the_listed_setup_paths()
    {
        var offenders = Sources()
            .Where(source => Creates(source.Text) > 0 && !allowList.ContainsKey(source.Path))
            .Select(source => source.Path);

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_allow_list_entry_still_calls_it_and_names_a_reason()
    {
        var calling = Sources().Where(source => Creates(source.Text) > 0).Select(source => source.Path).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(allowList.Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !calling.Contains(entry.Key)).Select(entry => entry.Key));
    }

    [Fact]
    public void The_scan_catches_every_form_of_the_call_and_passes_the_workflows_own_create()
    {
        const string caught = """
            var created = await userManager.CreateAsync(user, password);
            await _userManager.CreateAsync(user);
            await UserManager.CreateAsync(user, "x");
            await users.CreateAsync(user, NewPassword());
            await userStore.CreateAsync(user, cancellationToken);
            await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, Password);
            """;
        const string passed = """
            await users.CreateUserAsync(user, tenantId, password);
            await service.CreateApplicationUserAsync(draft, step, cancellationToken);
            await claims.CreateAsync(user);
            await roleManager.CreateAsync(new ApplicationRole { Name = "Admin" });
            // userManager.CreateAsync(user) in a comment is no call.
            """;

        Assert.Equal(6, Creates(caught));
        Assert.Equal(0, Creates(passed));
    }

    [Fact]
    public void Only_tenant_onboarding_and_the_seeder_call_the_onboarding_writer_and_only_it_and_the_system_accounts_add_user_rows()
    {
        var sources = Sources().ToList();
        var writerCallers = sources.Where(source => Count(OnboardingWriterCall(), source.Text) > 0).Select(source => source.Path).ToHashSet(StringComparer.Ordinal);
        var userAdders = sources.Where(source => Count(UserRowAdd(), source.Text) > 0).Select(source => source.Path).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(onboardingWriterCallers.Keys.Order(StringComparer.Ordinal), writerCallers.Order(StringComparer.Ordinal));
        Assert.Equal(userRowAdders.Keys.Order(StringComparer.Ordinal), userAdders.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(onboardingWriterCallers.Values.Concat(userRowAdders.Values), string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void The_scan_catches_every_call_of_the_onboarding_writer_and_every_direct_user_row()
    {
        const string writerCaught = """
            await TenantOnboardingWriter.WriteAsync(context, tenantId, owner, ct);
            await Staffora.Data.TenantOnboardingWriter.EnsureSystemOwnerAsync(context, tenant.Id, manager.Id);
            onboard: (context, id, ct) => TenantOnboardingWriter
                .EnsureRolePresetsAsync(context, id, ct);
            using static Staffora.Data.TenantOnboardingWriter;
            """;
        const string writerPassed = """
            // TenantOnboardingWriter.WriteAsync(context, tenantId, owner) in a comment is no call.
            /// <see cref="TenantOnboardingWriter.WriteAsync"/>
            var name = "TenantOnboardingWriter";
            await TenantOnboardingService.OnboardTenantAsync(request, ct);
            """;
        const string addCaught = """
            context.Users.Add(owner);
            await context.Users.AddAsync(user, ct);
            db.Users.AddRange(first, second);
            context.Users . Attach(user);
            context.Set<ApplicationUser>().Add(user);
            await context.Set< ApplicationUser >().AddAsync(user);
            """;
        const string addPassed = """
            // context.Users.Add(owner) in a comment is no call.
            context.Users.Remove(user);
            var found = await context.Users.FirstOrDefaultAsync(row => row.Id == id);
            context.TenantRoles.Add(role);
            context.Set<TenantRole>().Add(role);
            newUsers.Add(user);
            """;

        Assert.Equal(4, Count(OnboardingWriterCall(), writerCaught));
        Assert.Equal(0, Count(OnboardingWriterCall(), writerPassed));
        Assert.Equal(6, Count(UserRowAdd(), addCaught));
        Assert.Equal(0, Count(UserRowAdd(), addPassed));
    }

    private static int Count(Regex pattern, string source) => pattern.Matches(LineComment().Replace(source, string.Empty)).Count;

    private static int Creates(string source) => IdentityCreate().Matches(LineComment().Replace(source, string.Empty)).Count;

    private static IEnumerable<(string Path, string Text)> Sources() =>
        RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => productProjects.Any(project => source.Path.StartsWith(project, StringComparison.Ordinal)))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    [GeneratedRegex(@"(?:\b_?(?:\w*[Uu]ser(?:Manager|Store)\w*|users)|UserManager<\w+>>\(\))\s*\.\s*CreateAsync\s*\(")]
    private static partial Regex IdentityCreate();

    [GeneratedRegex(@"\bTenantOnboardingWriter\s*\.\s*[A-Z]\w*|using\s+static\s+[\w.]*\bTenantOnboardingWriter\b")]
    private static partial Regex OnboardingWriterCall();

    [GeneratedRegex(@"(?:\bUsers|\bSet\s*<\s*ApplicationUser\s*>\s*\(\s*\))\s*\.\s*(?:Add|AddRange|Attach|AttachRange)(?:Async)?\s*\(")]
    private static partial Regex UserRowAdd();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();
}
