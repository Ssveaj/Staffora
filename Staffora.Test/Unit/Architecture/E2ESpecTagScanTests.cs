using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class E2ESpecTagScanTests
{
    private static readonly string[] OtherTags = ["@smoke", "@dialog-host"];

    [Fact]
    public void Every_spec_carries_an_area_tag()
    {
        var areas = AreaTags();
        var untagged = Specs().Where(spec => !TagsOf(spec.Text).Any(areas.Contains)).Select(spec => spec.Name).ToList();

        Assert.Empty(untagged);
    }

    [Fact]
    public void Every_tag_a_spec_names_is_an_area_or_the_smoke_set()
    {
        var known = AreaTags().Concat(OtherTags).ToHashSet(StringComparer.Ordinal);
        var unknown = Specs().SelectMany(spec => TagsOf(spec.Text).Where(tag => !known.Contains(tag)).Select(tag => $"{spec.Name}: {tag}")).ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void The_smoke_set_is_small_and_present()
    {
        var declarations = Specs().Sum(spec => Regex.Matches(spec.Text, "'@smoke'").Count);

        Assert.InRange(declarations, 1, 15);
    }

    [Fact]
    public void Every_spec_on_the_dialog_host_holds_its_lock()
    {
        var hostSpecs = Specs().Where(spec => spec.Text.Contains("from './lib/dialog-host'", StringComparison.Ordinal)).ToList();
        var unlocked = hostSpecs.Where(spec => !spec.Text.Contains("lock: 'dialog-host'", StringComparison.Ordinal) || !TagsOf(spec.Text).Contains("@dialog-host")).Select(spec => spec.Name).ToList();

        Assert.NotEmpty(hostSpecs);
        Assert.Empty(unlocked);
    }

    [Fact]
    public void The_scan_reads_the_tags_of_a_sample()
    {
        const string sample = """
            test('one', { tag: '@staffing' }, async () => {});
            test.describe(`two`, { tag: ['@portal', '@smoke'], lock: 'dialog-host' }, () => {});
            test('three', { tag: width === 1280 ? ['@shell', '@smoke'] : '@shell' }, async () => {});
            """;

        Assert.Equal(["@portal", "@shell", "@smoke", "@staffing"], TagsOf(sample).Order(StringComparer.Ordinal));
        Assert.Contains("@coordination", AreaTags());
    }

    private static HashSet<string> TagsOf(string spec) =>
        [.. TagDetail().Matches(spec).SelectMany(detail => TagLiteral().Matches(detail.Groups["value"].Value).Select(literal => literal.Groups["tag"].Value))];

    private static HashSet<string> AreaTags()
    {
        var runner = File.ReadAllText(Path.Combine(RepoFiles.Root, "scripts", "e2e-run.mjs"));
        var list = AreaList().Match(runner);
        Assert.True(list.Success, "scripts/e2e-run.mjs declares no areaTags list.");
        return [.. TagLiteral().Matches(list.Groups["list"].Value).Select(literal => literal.Groups["tag"].Value)];
    }

    private static List<(string Name, string Text)> Specs() =>
        [.. Directory.GetFiles(Path.Combine(RepoFiles.Root, "tests", "e2e"), "*.spec.ts").Select(file => (Path.GetFileName(file), File.ReadAllText(file)))];

    [GeneratedRegex(@"\btag:\s*(?<value>[^}]*?)(?=,\s*lock:|\s*\})", RegexOptions.Singleline)]
    private static partial Regex TagDetail();

    [GeneratedRegex(@"'(?<tag>@[a-z0-9-]+)'")]
    private static partial Regex TagLiteral();

    [GeneratedRegex(@"export const areaTags = \[(?<list>[^\]]*)\]")]
    private static partial Regex AreaList();
}
