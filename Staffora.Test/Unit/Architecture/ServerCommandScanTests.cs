using System.Text.RegularExpressions;
using Staffora.Services.Authorization;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class ServerCommandScanTests
{
    private const string CommandBranch = "Staffora.Blazor/Helper/ServerCommands.cs";

    private static readonly string[] commands = [.. typeof(PlatformOwnerResetCommand).Assembly.GetTypes()
        .Where(type => type.Name.EndsWith("Command", StringComparison.Ordinal) && type is { IsClass: true, IsAbstract: false })
        .Select(type => type.Name)
        .Order(StringComparer.Ordinal)];

    [Fact]
    public void The_commands_are_found_by_name()
    {
        Assert.Contains(nameof(PlatformOwnerResetCommand), commands);
        Assert.Contains(nameof(AccountUnlockCommand), commands);
    }

    [Fact]
    public void No_file_of_the_web_app_but_the_command_branch_registers_resolves_or_injects_a_command()
    {
        var offenders = Sources()
            .Where(source => source.Path != CommandBranch && Uses(source.Text) > 0)
            .Select(source => source.Path);

        Assert.Empty(offenders);
        Assert.True(Uses(File.ReadAllText(Path.Combine(RepoFiles.Root, CommandBranch))) > 0, "The command branch no longer registers the commands.");
    }

    [Fact]
    public void Program_enters_the_command_branch_only_when_a_command_runs()
    {
        var program = LineComment().Replace(File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor/Program.cs")), string.Empty);

        Assert.Single(Regex.Matches(program, @"\bAddServerCommands\s*\("));
        Assert.Single(Regex.Matches(program, @"\bServerCommands\s*\.\s*RunAsync\s*\("));
        Assert.Equal(2, GuardedCall().Matches(program).Count);
    }

    [Fact]
    public void The_scan_catches_every_form_of_registering_resolving_or_injecting_a_command()
    {
        const string caught = """
            builder.Services.AddScoped<PlatformOwnerResetCommand>();
            services.AddTransient< AccountUnlockCommand >();
            services.TryAddScoped<Staffora.Services.Authorization.AccountUnlockCommand>();
            var reset = scope.ServiceProvider.GetRequiredService<PlatformOwnerResetCommand>();
            var unlock = provider.GetService<AccountUnlockCommand>();
            var type = typeof(PlatformOwnerResetCommand);
            @inject PlatformOwnerResetCommand Reset
            @inject Staffora.Services.Authorization.AccountUnlockCommand Unlock
            [Inject] private PlatformOwnerResetCommand Reset { get; set; } = default!;
            app.MapPost("/reset", async (string email, PlatformOwnerResetCommand command) => await command.ResetAsync(email));
            public sealed class ResetEndpoint(AccountUnlockCommand unlock)
            """;
        const string passed = """
            var command = args is [PlatformBootstrap.Command or AccountUnlockCommand.Command or PlatformOwnerResetCommand.Command, ..] ? args[0] : null;
            PlatformOwnerResetCommand.Command => $"Usage: {PlatformOwnerResetCommand.Command} <email>",
            // builder.Services.AddScoped<PlatformOwnerResetCommand>(); in a comment is no registration.
            builder.Services.AddScoped<PlatformBootstrap>();
            if (ServerCommands.Runs(command)) { builder.Services.AddServerCommands(); }
            """;

        Assert.Equal(11, Uses(caught));
        Assert.Equal(0, Uses(passed));
    }

    private static int Uses(string source)
    {
        var text = LineComment().Replace(source, string.Empty);
        var name = $@"(?:[\w.]+\.)?(?:{string.Join('|', commands.Select(Regex.Escape))})";
        var patterns = new[]
        {
            $@"\b(?:Try)?Add(?:Keyed)?(?:Scoped|Transient|Singleton)\s*<\s*{name}\s*[,>]",
            $@"\bGet(?:Required)?(?:Keyed)?Services?\s*<\s*{name}\s*>",
            $@"\btypeof\s*\(\s*{name}\s*\)",

            $@"@inject\s+{name}\s+\w+",
            $@"\[Inject\][^;{{]*\b{name}\s+\w+",

            $@"[(,]\s*(?:\[\w+\]\s*)?{name}\s+\w+\s*[,)]",
        };

        return patterns.Sum(pattern => Regex.Matches(text, pattern).Count);
    }

    private static IEnumerable<(string Path, string Text)> Sources() =>
        RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    [GeneratedRegex(@"if\s*\(\s*ServerCommands\s*\.\s*Runs\s*\(\s*command\s*\)\s*\)\s*\{\s*(?:builder\.Services\.AddServerCommands\s*\(\s*\)|Environment\.ExitCode\s*=\s*await\s+ServerCommands\s*\.\s*RunAsync\s*\([^;]*\))\s*;\s*(?:return\s*;\s*)?\}")]
    private static partial Regex GuardedCall();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();
}
