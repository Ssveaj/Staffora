using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class NativeSelectScanTests
{
    private static readonly Dictionary<string, string> allowed = new(StringComparer.Ordinal);

    [Fact]
    public void No_component_draws_a_native_select_outside_the_allow_list()
    {
        var offenders = BlazorSources()
            .Where(source => !allowed.ContainsKey(source.Key))
            .SelectMany(source => NativeSelects(source.Value).Select(tag => $"{source.Key}: {tag}"))
            .ToList();

        Assert.True(offenders.Count == 0, $"Native selects (use a RadzenDropDown named by its label and FieldLabel):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void Every_allow_listed_file_still_draws_a_native_select_and_names_a_reason()
    {
        var sources = BlazorSources();

        Assert.All(allowed, allowed =>
        {
            Assert.False(string.IsNullOrWhiteSpace(allowed.Value), $"{allowed.Key} names no reason");
            Assert.True(sources.TryGetValue(allowed.Key, out var text) && NativeSelects(text).Count > 0, $"{allowed.Key} no longer draws a native select; take it off the list");
        });
    }

    [Fact]
    public void The_scan_catches_a_select_and_an_InputSelect_and_passes_a_Radzen_drop_down_and_comments()
    {
        const string sample = """
            <select id="unit" name="unit" @onchange="Chosen"><option value="">All</option></select>
            <SELECT name="loud"></SELECT>
            <InputSelect @bind-Value="Model.Kind"><option>One</option></InputSelect>
            <RadzenDropDown TValue="Guid" Name="unit" Data="@Units" />
            <RadzenSelectBar TValue="int" />
            <p>Select a unit, or selected: <span class="select-hint">none</span></p>
            @* <select name="commented-out"></select> *@
            """;

        Assert.Equal(["<select", "<SELECT", "<InputSelect"], NativeSelects(sample));
    }

    [Fact]
    public void The_scan_reads_the_apps_components_and_pages()
    {
        var sources = BlazorSources();

        Assert.Contains("Staffora.Blazor/Components/Coordination/CalendarRail.razor", sources.Keys);
        Assert.Contains("Staffora.Blazor/Components/Common/Dialogs/AbsenceDialog.razor", sources.Keys);
    }

    private static List<string> NativeSelects(string razor) =>
        [.. SelectStart().Matches(RazorComment().Replace(razor, string.Empty)).Select(match => match.Value)];

    private static Dictionary<string, string> BlazorSources() =>
        RepoFiles.CommittedTextFiles(".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .ToDictionary(source => source.Path, source => File.ReadAllText(source.File), StringComparer.Ordinal);

    [GeneratedRegex(@"<(?:select|InputSelect)(?=[\s>/])", RegexOptions.IgnoreCase)]
    private static partial Regex SelectStart();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();
}
