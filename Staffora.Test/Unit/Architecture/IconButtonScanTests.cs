using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class IconButtonScanTests
{
    private static readonly Dictionary<string, string> wrappers = new(StringComparer.Ordinal);

    [Fact]
    public void Every_icon_only_button_has_an_aria_label()
    {
        var offenders = BlazorSources()
            .Where(source => !wrappers.ContainsKey(source.Key))
            .SelectMany(source => UnlabelledIconButtons(source.Value).Select(button => $"{source.Key}: {button}"))
            .ToList();

        Assert.True(offenders.Count == 0, $"Icon-only buttons and links without an aria-label:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void Every_use_of_a_wrapper_passes_text()
    {
        var sources = BlazorSources();

        Assert.All(wrappers, wrapper =>
        {
            Assert.True(sources.ContainsKey(wrapper.Key), $"{wrapper.Key} no longer exists");
            var uses = sources.Values
                .SelectMany(text => Regex.Matches(RazorComment().Replace(text, string.Empty), $@"<{wrapper.Value}\b[^>]*>(?<content>.*?)</{wrapper.Value}>", RegexOptions.Singleline))
                .Select(use => use.Groups["content"].Value)
                .ToList();

            Assert.NotEmpty(uses);
            Assert.All(uses, content => Assert.True(HasText(content), $"A {wrapper.Value} shows no text: {content}"));
        });
    }

    [Fact]
    public void No_Radzen_button_shows_an_icon_beside_its_text()
    {
        var offenders = BlazorSources()
            .SelectMany(source => IconBesideText(source.Value).Select(button => $"{source.Key}: {button}"))
            .ToList();

        Assert.True(offenders.Count == 0, $"Radzen buttons with an Icon beside their text (use IconTextButton):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void The_scan_catches_a_Radzen_button_with_an_icon_and_text_and_passes_the_others()
    {
        const string sample = """
            <RadzenButton Icon="save" Text="Save" Click="@(() => x > 1 ? A() : B())" />
            <RadzenButton Text="@Label" Icon="@(_busy ? BusyIcon : IconName)" />
            <RadzenButton Icon="save">Save</RadzenButton>
            <RadzenButton Icon="close" aria-label="Close" />
            <RadzenButton Text="Cancel" />
            <RadzenButton Icon="add" Text="" aria-label="Add" />
            <IconTextButton Icon="save" Text="Save" />
            <RadzenButton ButtonStyle="@ButtonStyle">@ChildContent</RadzenButton>
            @* <RadzenButton Icon="info" Text="Info" /> in a comment is not a button *@
            """;

        Assert.Equal(
            [
                "<RadzenButton Icon=\"save\" Text=\"Save\" Click=\"@(() => x > 1 ? A() : B())\" />",
                "<RadzenButton Text=\"@Label\" Icon=\"@(_busy ? BusyIcon : IconName)\" />",
                "<RadzenButton Icon=\"save\">",
            ],
            IconBesideText(sample));
    }

    private static List<string> IconBesideText(string razor)
    {
        var source = RazorComment().Replace(razor, string.Empty);
        var offenders = new List<string>();
        foreach (Match start in ButtonStart().Matches(source))
        {
            if (start.Groups["name"].Value != "RadzenButton")
            {
                continue;
            }

            var tagEnd = EndOfTag(source, start.Index + start.Length);
            if (tagEnd < 0)
            {
                continue;
            }

            var tag = source[start.Index..(tagEnd + 1)];
            var content = string.Empty;
            if (!tag.EndsWith("/>", StringComparison.Ordinal))
            {
                var close = source.IndexOf("</RadzenButton>", tagEnd, StringComparison.Ordinal);
                content = close < 0 ? string.Empty : source[(tagEnd + 1)..close];
            }

            if (HasAttribute(tag, "Icon") && (HasValue(tag, "Text") || HasText(content)))
            {
                offenders.Add(Regex.Replace(tag, @"\s+", " "));
            }
        }

        return offenders;
    }

    private static Dictionary<string, string> BlazorSources() =>
        RepoFiles.CommittedTextFiles(".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .ToDictionary(source => source.Path, source => File.ReadAllText(source.File), StringComparer.Ordinal);

    [Fact]
    public void The_scan_catches_unlabelled_icon_buttons_and_passes_labelled_and_text_buttons()
    {
        const string sample = """
            <RadzenButton Icon="delete" Click="@(() => DeleteAsync(row))" />
            <RadzenButton Icon="delete" aria-label="Delete row" Click="@(() => x > 1 ? A() : B())" />
            <RadzenButton Icon="add" Text="New role" />
            <RadzenButton Icon="close"></RadzenButton>
            <RadzenButton Icon="save">Save</RadzenButton>
            <button type="button" @onclick="Toggle"><MaterialSymbol Name="menu" /></button>
            <button type="button" aria-label="Show the menu" @onclick="Toggle"><MaterialSymbol Name="menu" /></button>
            <button type="button"><span class="material-symbols-outlined" aria-hidden="true">help</span></button>
            <button type="submit"><MaterialSymbol Name="logout" /><span>Logout</span></button>
            <button type="button">@Label</button>
            <RadzenButton Icon="add" Text="" />
            <button type="button" aria-label=""><MaterialSymbol Name="menu" /></button>
            <button type="button">@ChildContent</button>
            <button type="button">@IconTemplate</button>
            <a href="/"><MaterialSymbol Name="home" /></a>
            <a href="/" aria-label="Start page"><MaterialSymbol Name="home" /></a>
            <a href="/"><MaterialSymbol Name="home" /><span>Start page</span></a>
            <NavLink href="/me"><MaterialSymbol Name="person" /></NavLink>
            <NavLink href="/me"><MaterialSymbol Name="person" /><span>@item.Text</span></NavLink>
            <abbr title="not a link">x</abbr>
            @* <RadzenButton Icon="info" /> in a comment is not a button *@
            """;

        Assert.Equal(
            [
                "<RadzenButton Icon=\"delete\" Click=\"@(() => DeleteAsync(row))\" />",
                "<RadzenButton Icon=\"close\">",
                "<button type=\"button\" @onclick=\"Toggle\">",
                "<button type=\"button\">",
                "<RadzenButton Icon=\"add\" Text=\"\" />",
                "<button type=\"button\" aria-label=\"\">",
                "<button type=\"button\">",
                "<button type=\"button\">",
                "<a href=\"/\">",
                "<NavLink href=\"/me\">",
            ],
            UnlabelledIconButtons(sample));
    }

    [Theory]
    [InlineData("Staffora.Blazor/Components/Common/Dialogs/StateEntity/DialogSideTabs.razor", 1)]
    [InlineData("Staffora.Blazor/Components/Common/Dialogs/StateEntity/StateEntityDialogHeader.razor", 2)]
    public void The_scan_covers_the_dialogs_folder_side_tabs_and_header_buttons(string file, int iconOnly)
    {
        var source = RazorComment().Replace(BlazorSources()[file], string.Empty);
        Assert.Empty(UnlabelledIconButtons(source));
        var unnamed = Regex.Replace(source, @"\saria-label=""[^""]*""", string.Empty);
        Assert.Equal(iconOnly, UnlabelledIconButtons(unnamed).Count);
    }

    private static List<string> UnlabelledIconButtons(string razor)
    {
        var source = RazorComment().Replace(razor, string.Empty);
        var offenders = new List<string>();

        foreach (Match start in ButtonStart().Matches(source))
        {
            var name = start.Groups["name"].Value;
            var tagEnd = EndOfTag(source, start.Index + start.Length);
            if (tagEnd < 0)
            {
                continue;
            }

            var tag = source[start.Index..(tagEnd + 1)];
            var selfClosing = tag.EndsWith("/>", StringComparison.Ordinal);
            var content = string.Empty;
            if (!selfClosing)
            {
                var close = source.IndexOf($"</{name}>", tagEnd, StringComparison.Ordinal);
                content = close < 0 ? string.Empty : source[(tagEnd + 1)..close];
            }

            var iconOnly = name == "RadzenButton"
                ? HasAttribute(tag, "Icon") && !HasValue(tag, "Text") && !HasText(content)
                : !HasText(content);

            if (iconOnly && !HasValue(tag, "aria-label") && !HasValue(tag, "aria-labelledby"))
            {
                offenders.Add(Regex.Replace(tag, @"\s+", " "));
            }
        }

        return offenders;
    }

    private static int EndOfTag(string source, int from)
    {
        var quote = '\0';
        var depth = 0;
        for (var index = from; index < source.Length; index++)
        {
            var character = source[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (character == '>' && depth <= 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasAttribute(string tag, string attribute) =>
        Regex.IsMatch(tag, $@"\s{Regex.Escape(attribute)}\s*=", RegexOptions.IgnoreCase);

    private static bool HasValue(string tag, string attribute) =>
        Regex.Match(tag, $@"\s{Regex.Escape(attribute)}\s*=\s*""(?<value>[^""]*)""", RegexOptions.IgnoreCase) is { Success: true } match
            ? !string.IsNullOrWhiteSpace(match.Groups["value"].Value)
            : HasAttribute(tag, attribute);

    private static bool HasText(string content)
    {
        var withoutIcons = IconTag().Replace(content, string.Empty);
        withoutIcons = HiddenElement().Replace(withoutIcons, string.Empty);
        withoutIcons = FragmentWithoutText().Replace(withoutIcons, string.Empty);
        return !string.IsNullOrWhiteSpace(AnyTag().Replace(withoutIcons, string.Empty));
    }

    [GeneratedRegex(@"<(?<name>RadzenButton|button|a|NavLink)\b(?![-:])")]
    private static partial Regex ButtonStart();

    [GeneratedRegex(@"<(?:MaterialSymbol|RadzenIcon)\b[^>]*/>|<(?<tag>MaterialSymbol|RadzenIcon)\b[^>]*>.*?</\k<tag>>", RegexOptions.Singleline)]
    private static partial Regex IconTag();

    [GeneratedRegex(@"<(?<tag>\w+)\b[^>]*aria-hidden=""true""[^>]*>.*?</\k<tag>>", RegexOptions.Singleline)]
    private static partial Regex HiddenElement();

    [GeneratedRegex(@"@(?:ChildContent|[\w.]*Icon\w*)\b")]
    private static partial Regex FragmentWithoutText();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();
}
