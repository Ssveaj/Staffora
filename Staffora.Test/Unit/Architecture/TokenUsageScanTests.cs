using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class TokenUsageScanTests
{
    private static readonly Dictionary<string, string> allowed = new(StringComparer.Ordinal);

    [Fact]
    public void No_component_stylesheet_writes_a_hex_colour_outside_the_allow_list()
    {
        var offenders = Stylesheets()
            .Where(sheet => !allowed.ContainsKey(sheet.Key))
            .SelectMany(sheet => HexColours(sheet.Value).Select(colour => $"{sheet.Key}: {colour}"))
            .ToList();

        Assert.True(offenders.Count == 0, $"Hex colours outside app.tokens.css (read a token instead):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void Every_allow_listed_stylesheet_still_writes_a_hex_colour_and_names_a_reason()
    {
        var sheets = Stylesheets();
        var stale = allowed
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !sheets.TryGetValue(entry.Key, out var text) || HexColours(text).Count == 0)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void The_scan_catches_a_hex_colour_in_a_declaration_and_passes_tokens_ids_and_comments()
    {
        const string sample = """
            /* A comment may name #ff0000 freely. */
            #components-reconnect-modal { color: var(--color-text); }
            #add, a:hover #fade { border: 1px solid var(--color-border); }
            .badge { color: #fff; background: color-mix(in srgb, #0f766e 8%, white); }
            .bar { box-shadow: 0 0 0 2px #ABCDEF80; }
            """;

        Assert.Equal(["#fff", "#0f766e", "#ABCDEF80"], HexColours(sample));
    }

    [Fact]
    public void The_scan_reads_the_component_stylesheets_and_the_shared_ones()
    {
        var sheets = Stylesheets();

        Assert.Contains("Staffora.Blazor/wwwroot/app.css", sheets.Keys);
        Assert.Contains("Staffora.Blazor/Layout/NavMenu.razor.css", sheets.Keys);
        Assert.DoesNotContain("Staffora.Blazor/wwwroot/app.tokens.css", sheets.Keys);
    }

    private static List<string> HexColours(string css) =>
        [.. HexInDeclaration().Matches(CssComment().Replace(css, string.Empty)).Select(match => match.Value)];

    private static Dictionary<string, string> Stylesheets() =>
        RepoFiles.CommittedTextFiles(".css")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(sheet => sheet.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal) && !sheet.Path.Contains("/wwwroot/lib/", StringComparison.Ordinal))
            .Where(sheet => sheet.Path.EndsWith(".razor.css", StringComparison.Ordinal) || sheet.Path is "Staffora.Blazor/wwwroot/app.css" or "Staffora.Blazor/wwwroot/app.typography.css")
            .ToDictionary(sheet => sheet.Path, sheet => File.ReadAllText(sheet.File), StringComparer.Ordinal);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"(?<=:[^;{}]*)#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})\b(?![^;{}]*\{)")]
    private static partial Regex HexInDeclaration();
}
