using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Staffora.Services.Demo;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class DemoScanTests
{
    private static readonly string[] demoTypes = [.. typeof(DemoSessions).Assembly.GetTypes()
        .Where(type => type.Namespace == typeof(DemoSessions).Namespace && !type.Name.StartsWith('<') && !type.IsNested)
        .Select(type => type.Name)
        .Order(StringComparer.Ordinal)];

    private const string Startup = "Staffora.Blazor/Program.cs";

    [Fact]
    public void The_demo_types_are_found_by_their_namespace()
    {
        Assert.Contains(nameof(DemoSessions), demoTypes);
        Assert.Contains(nameof(DemoDataSeeder), demoTypes);
        Assert.Contains(nameof(CompanyASetup), demoTypes);
        Assert.Contains(nameof(CoordinationShowcase), demoTypes);
    }

    [Fact]
    public void No_product_file_outside_the_demo_names_it_but_the_startup()
    {
        var offenders = RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal)
                || source.Path.StartsWith("Staffora.Data/", StringComparison.Ordinal)
                || source.Path.StartsWith("Staffora.Core/", StringComparison.Ordinal)
                || (source.Path.StartsWith("Staffora.Services/", StringComparison.Ordinal) && !source.Path.StartsWith("Staffora.Services/Demo/", StringComparison.Ordinal)))
            .Where(source => source.Path != Startup && Uses(File.ReadAllText(source.File)) > 0)
            .Select(source => source.Path);

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_startup_registers_the_demo_seeder_and_resolves_it_only_in_its_development_block()
    {
        var program = LineComment().Replace(File.ReadAllText(Path.Combine(RepoFiles.Root, Startup)), string.Empty);

        Assert.Equal(2, Uses(program));
        Assert.Single(Regex.Matches(program, @"\bAddScoped\s*<\s*Staffora\.Services\.Demo\.DemoDataSeeder\s*>\s*\(\s*\)"));
        Assert.Single(DevelopmentResolve().Matches(program));
    }

    [Fact]
    public void The_scan_catches_every_way_of_naming_the_demo()
    {
        const string caught = """
            using Staffora.Services.Demo;
            @using Staffora.Services.Demo
            await using var scope = await DemoSessions.SignInAsync(services, email);
            var seeder = provider.GetRequiredService<Staffora.Services.Demo.DemoDataSeeder>();
            @inject DemoDataSeeder Demo
            var built = await CompanyASetup.SeedAsync(services, ownerId, _ => null, token);
            """;
        const string passed = """
            // DemoSessions.SignInAsync(services, email) in a comment is no call.
            var seeder = provider.GetRequiredService<DatabaseSeeder>();
            var sessions = new DemoSessionsOfMyOwn();
            """;

        Assert.Equal(6, Uses(caught));
        Assert.Equal(0, Uses(passed));
    }

    [Fact]
    public async Task A_demo_session_refuses_outside_development_before_it_reads_anything()
    {
        foreach (var environment in new IHostEnvironment?[] { new HostingEnvironment { EnvironmentName = Environments.Production }, new HostingEnvironment { EnvironmentName = Environments.Staging }, new HostingEnvironment { EnvironmentName = "Test" }, null })
        {
            var services = new ServiceCollection();
            if (environment is not null)
            {
                services.AddSingleton(environment);
            }

            await using var provider = services.BuildServiceProvider();
            var byEmail = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSessions.SignInAsync(provider, "someone@example.com"));
            var byId = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSessions.SignInAsync(provider, Guid.NewGuid()));
            Assert.Equal((DemoSessions.NotDevelopment, DemoSessions.NotDevelopment), (byEmail.Message, byId.Message));
        }
    }

    [Fact]
    public void The_demo_table_in_the_features_doc_names_every_sign_in_and_a_staff_example_with_its_pattern()
    {
        var features = File.ReadAllText(Path.Combine(RepoFiles.Root, "docs", "features", "demo-data.md"));
        var start = features.IndexOf("## Demo data", StringComparison.Ordinal);
        var end = features.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = features[start..(end < 0 ? features.Length : end)];

        Assert.Equal(5, DemoDataSeeder.Businesses.Count);
        foreach (var business in DemoDataSeeder.Businesses)
        {
            Assert.All([.. business.Owners, .. business.Coordinators, business.Portal.CustomerAdministrator, business.Portal.Orderer], person => Assert.Contains($"`{person.Email}`", section, StringComparison.Ordinal));
            Assert.All(business.StaffMembers, staff => Assert.Contains(staff.Person.Name, section, StringComparison.Ordinal));
            Assert.Contains($"`{business.StaffMembers[0].Person.Email}`, {business.StaffMembers[0].Pattern}", section, StringComparison.Ordinal);
        }
    }

    private static int Uses(string source)
    {
        var text = LineComment().Replace(source, string.Empty);
        var names = $@"\b(?:Staffora\.Services\.Demo\b(?!\.(?:{string.Join('|', demoTypes.Select(Regex.Escape))})\b)|(?:Staffora\.Services\.Demo\.)?(?:{string.Join('|', demoTypes.Select(Regex.Escape))})\b)";
        return Regex.Matches(text, names).Count;
    }

    [GeneratedRegex(@"if\s*\([^\n]*\bapp\.Environment\.IsDevelopment\(\)\s*\)\s*\{\s*var\s+\w+\s*=\s*await\s+scope\.ServiceProvider\.GetRequiredService\s*<\s*Staffora\.Services\.Demo\.DemoDataSeeder\s*>\s*\(\s*\)")]
    private static partial Regex DevelopmentResolve();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();
}
