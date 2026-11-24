using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class PageChromeScanTests
{
    private const string SystemPageReason = "a system page: one SystemPage card, the same in the signed-in layout and in the plain frame";
    private const string SignInReason = "the sign-in flow, before any session: its own card in the plain frame (the sign-in page beside its brand panel)";
    private const string HomeReason = "a home: its own head (HomeHead: the eyebrow, the title as the h1, the help popover, the date and the refresh, and the document title) opens its bands (docs/features/coordination-and-homes.md, \"Homes\")";

    private static readonly Dictionary<string, string> pagesWithoutHeader = new(StringComparer.Ordinal)
    {
        ["Staffora.Blazor/Pages/Home.razor"] = "the start page renders nothing; it sends each session on to where its work starts",
        ["Staffora.Blazor/Pages/StatusPage.razor"] = SystemPageReason,
        ["Staffora.Blazor/Pages/NotFound.razor"] = SystemPageReason,
        ["Staffora.Blazor/Pages/Error.razor"] = SystemPageReason,
        ["Staffora.Blazor/Components/Account/Pages/AccessDenied.razor"] = SystemPageReason,
        ["Staffora.Blazor/Components/Account/Pages/Lockout.razor"] = SystemPageReason,
        ["Staffora.Blazor/Components/Account/Pages/Login.razor"] = SignInReason,
        ["Staffora.Blazor/Pages/Staffing/Coordination.razor"] = "the coordination workspace, the one full-bleed page: a tool filling the viewport under the top row, its own toolbar under the breadcrumb (ADR 0026)",
        ["Staffora.Blazor/Pages/Me/MyWork.razor"] = HomeReason,
        ["Staffora.Blazor/Pages/Staffing/CoordinatorOverview.razor"] = HomeReason,
        ["Staffora.Blazor/Pages/TenantAdmin/TenantManagementOverview.razor"] = HomeReason,
        ["Staffora.Blazor/Pages/Portal/PortalHome.razor"] = HomeReason,
        ["Staffora.Blazor/Pages/SuperAdmin/SuperAdminOverview.razor"] = HomeReason,
    };

    private static readonly string[] systemPages =
    [
        "Staffora.Blazor/Pages/StatusPage.razor",
        "Staffora.Blazor/Pages/NotFound.razor",
        "Staffora.Blazor/Pages/Error.razor",
        "Staffora.Blazor/Components/Account/Pages/AccessDenied.razor",
        "Staffora.Blazor/Components/Account/Pages/Lockout.razor",
    ];

    [Fact]
    public void Every_routable_page_uses_the_shared_header_or_is_allow_listed()
    {
        var sources = BlazorSources();
        var offenders = Pages(sources)
            .Where(page => !pagesWithoutHeader.ContainsKey(page) && !UsesSharedHeader(page, sources))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_shared_header_draws_no_band_fills_the_top_row_with_its_title_and_help_and_keeps_its_actions_in_the_page()
    {
        var sources = BlazorSources();
        var header = WithoutComments(sources["Staffora.Blazor/Components/Common/Layout/PageHeader.razor"]);
        var crumb = Regex.Match(header, "<SectionContent SectionName=\"@AppBreadcrumb.PageSection\">(?<body>.*?)</SectionContent>", RegexOptions.Singleline);

        Assert.True(crumb.Success, "PageHeader fills the breadcrumb's last step");
        Assert.Contains("<h1", crumb.Groups["body"].Value);
        Assert.Contains("data-page-help-toggle", crumb.Groups["body"].Value);
        Assert.DoesNotContain("@Actions", crumb.Groups["body"].Value);
        Assert.Matches(new Regex("<div class=\"page-header__lead\">.*<div class=\"page-header__actions\">\\s*@Actions", RegexOptions.Singleline), header);
        Assert.DoesNotContain("<header", header);
        Assert.Single(Regex.Matches(header, "<h1"));

        Assert.Contains("<SectionOutlet SectionName=\"@PageSection\" />", WithoutComments(sources["Staffora.Blazor/Layout/AppBreadcrumb.razor"]));
        Assert.DoesNotContain("SectionOutlet", WithoutComments(sources["Staffora.Blazor/Layout/AppShell.razor"]));
    }

    [Fact]
    public void Every_allow_listed_page_is_a_page_without_the_header_and_names_a_reason()
    {
        var sources = BlazorSources();
        var pages = Pages(sources).ToHashSet(StringComparer.Ordinal);
        var stale = pagesWithoutHeader
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !pages.Contains(entry.Key) || UsesSharedHeader(entry.Key, sources))
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void Every_home_allow_listed_for_its_own_head_opens_with_the_home_head()
    {
        var sources = BlazorSources();
        var homes = pagesWithoutHeader.Where(entry => entry.Value == HomeReason).Select(entry => entry.Key).ToList();

        Assert.NotEmpty(homes);
        Assert.All(homes, page => Assert.Matches(HomeHead(), WithoutComments(sources[page])));
    }

    [Fact]
    public void The_header_scan_finds_the_header_in_the_page_or_in_its_layout_and_ignores_comments()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App/Pages/Own.razor"] = "@page \"/own\"\n<PageScaffold><Header><PageHeader Title=\"Own\" /></Header></PageScaffold>",
            ["App/Pages/Bare.razor"] = "@page \"/bare\"\n<h1>Bare</h1>",
            ["App/Pages/Commented.razor"] = "@page \"/commented\"\n@* <PageHeader Title=\"Not really\" /> *@\n<h1>Commented</h1>",
            ["App/Pages/Account/Settings.razor"] = "@page \"/account/settings\"\n<h3>Settings</h3>",
            ["App/Pages/Account/_Imports.razor"] = "@layout AccountLayout",
            ["App/Shared/AccountLayout.razor"] = "@inherits LayoutComponentBase\n<PageHeader Title=\"Account\" />\n@Body",
            ["App/Pages/Framed.razor"] = "@page \"/framed\"\n@layout AccountLayout\n<p>Framed</p>",
        };

        Assert.Equal(
            ["App/Pages/Account/Settings.razor", "App/Pages/Framed.razor", "App/Pages/Own.razor"],
            Pages(sources).Where(page => UsesSharedHeader(page, sources)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_system_page_is_a_SystemPage_card_inside_the_main_layout()
    {
        var sources = BlazorSources();

        Assert.All(systemPages, page =>
        {
            var text = WithoutComments(sources[page]);
            Assert.Contains("<SystemPage", text);
            Assert.True(LayoutOf(page, sources) is null or "MainLayout", $"{page} names the layout {LayoutOf(page, sources)}");
        });
    }
    [Fact]
    public void The_profile_page_is_served_at_its_spelled_out_address()
    {
        var page = BlazorSources()["Staffora.Blazor/Pages/Me/MyProfile.razor"];

        Assert.Equal(["/me/profile"], PageRoute().Matches(page).Select(match => match.Groups["route"].Value));
    }

    private static bool UsesSharedHeader(string page, IReadOnlyDictionary<string, string> sources)
    {
        if (SharedHeader().IsMatch(WithoutComments(sources[page])))
        {
            return true;
        }

        return LayoutOf(page, sources) is { } layout
            && sources.FirstOrDefault(source => source.Key.EndsWith($"/{layout}.razor", StringComparison.Ordinal)).Value is { } layoutText
            && SharedHeader().IsMatch(WithoutComments(layoutText));
    }

    private static string? LayoutOf(string page, IReadOnlyDictionary<string, string> sources)
    {
        if (LayoutDirective().Match(WithoutComments(sources[page])) is { Success: true } own)
        {
            return own.Groups["layout"].Value;
        }

        for (var folder = Folder(page); folder.Contains('/'); folder = Folder(folder))
        {
            if (sources.TryGetValue($"{folder}/_Imports.razor", out var imports) && LayoutDirective().Match(WithoutComments(imports)) is { Success: true } imported)
            {
                return imported.Groups["layout"].Value;
            }
        }

        return null;
    }

    private static string Folder(string path) => path[..path.LastIndexOf('/')];

    private static IEnumerable<string> Pages(IReadOnlyDictionary<string, string> sources) =>
        sources.Where(source => PageRoute().IsMatch(WithoutComments(source.Value))).Select(source => source.Key);

    private static Dictionary<string, string> BlazorSources() =>
        RepoFiles.CommittedTextFiles(".razor")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => source.Path.StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .ToDictionary(source => source.Path, source => File.ReadAllText(source.File), StringComparer.Ordinal);

    private static string WithoutComments(string source) => Comment().Replace(source, string.Empty);

    [GeneratedRegex(@"^\s*@page\s+""(?<route>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex PageRoute();

    [GeneratedRegex(@"^\s*@layout\s+(?:[\w.]+\.)?(?<layout>\w+)\s*$", RegexOptions.Multiline)]
    private static partial Regex LayoutDirective();

    [GeneratedRegex(@"<PageHeader\b")]
    private static partial Regex SharedHeader();

    [GeneratedRegex(@"<HomeHead\b")]
    private static partial Regex HomeHead();

    [GeneratedRegex(@"@\*.*?\*@|(?<!:)//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
