using System.Text.RegularExpressions;
using Staffora.Blazor.Layout;
using Staffora.Core.Entities.Tenancy;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class NavigationGateTests
{
    [Fact]
    public void Every_tenant_link_shows_to_exactly_the_permissions_its_page_gates_on()
    {
        var pages = RoutedPages();
        var links = AppNavigation.Tenant.Concat(AppNavigation.Portal).SelectMany(section => section.Items).ToList();

        Assert.NotEmpty(links);
        Assert.All(links, link =>
        {
            Assert.True(pages.TryGetValue(link.Path, out var source), $"No page is routed at {link.Path}.");
            Assert.Equal(link.AnyOf.Order(), PageGate(source!).Order());
        });
    }

    [Fact]
    public void The_gate_scan_reads_a_permission_a_list_a_field_and_no_gate()
    {
        const string single = "@page \"/a\"\n@attribute [Authorize]\n@inject X Y\n\n<PermissionView Permission=\"RolePermission.UsersView\">\n<PageScaffold />\n</PermissionView>";
        const string inline = "@page \"/b\"\n<PermissionView AnyOf=\"new[] { RolePermission.UsersView, RolePermission.OrganizationUnitsView }\"><p /></PermissionView>";
        const string field = "@page \"/c\"\n<PermissionView AnyOf=\"gate\"><p /></PermissionView>\n@code {\n    private static readonly RolePermission[] gate =\n    [\n        RolePermission.RolesView,\n        RolePermission.RolesEdit\n    ];\n}";
        const string open = "@page \"/d\"\n<PageScaffold>\n<PermissionView Permission=\"RolePermission.UsersCreate\"><button /></PermissionView>\n</PageScaffold>";
        const string commented = "@page \"/e\"\n@* <PermissionView Permission=\"RolePermission.UsersView\"> *@\n<PageScaffold />";

        Assert.Equal([RolePermission.UsersView], PageGate(single));
        Assert.Equal([RolePermission.UsersView, RolePermission.OrganizationUnitsView], PageGate(inline));
        Assert.Equal([RolePermission.RolesView, RolePermission.RolesEdit], PageGate(field));
        Assert.Empty(PageGate(open));
        Assert.Empty(PageGate(commented));
    }

    private static List<RolePermission> PageGate(string source)
    {
        var text = Comment().Replace(source, string.Empty);
        var markup = Directive().Replace(text, string.Empty).TrimStart();
        if (GateStart().Match(markup) is not { Success: true } gate)
        {
            return [];
        }

        var value = gate.Groups["value"].Value;
        if (!value.Contains("RolePermission.", StringComparison.Ordinal))
        {
            var initializer = new Regex($@"\b{Regex.Escape(value.Trim())}\s*=(?<list>[^;]*);").Match(text);
            value = initializer.Groups["list"].Value;
        }

        return [.. PermissionName().Matches(value).Select(match => Enum.Parse<RolePermission>(match.Groups["name"].Value))];
    }

    private static Dictionary<string, string> RoutedPages() =>
        RepoFiles.CommittedTextFiles(".razor")
            .Where(file => Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/').StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .SelectMany(text => PageRoute().Matches(Comment().Replace(text, string.Empty)).Select(match => (Route: match.Groups["route"].Value, Text: text)))
            .ToDictionary(page => page.Route, page => page.Text, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"@\*.*?\*@|(?<!:)//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^\s*@(?:page|attribute|inject|using|layout|implements|inherits|rendermode)\b[^\n]*$", RegexOptions.Multiline)]
    private static partial Regex Directive();

    [GeneratedRegex(@"^<PermissionView\s[^>]*?\b(?:Permission|AnyOf)=""(?<value>[^""]*)""")]
    private static partial Regex GateStart();

    [GeneratedRegex(@"RolePermission\.(?<name>\w+)")]
    private static partial Regex PermissionName();

    [GeneratedRegex(@"^\s*@page\s+""(?<route>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex PageRoute();
}
