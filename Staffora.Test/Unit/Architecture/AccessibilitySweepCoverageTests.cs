using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class AccessibilitySweepCoverageTests
{
    private const string UnknownAddress = "/no-such-page";

    [Fact]
    public void Every_routable_page_is_swept_or_skipped_with_a_reason()
    {
        var (visited, skipped) = SweepLists(SweepLib());
        var routes = Routes();

        var unswept = routes.Where(route => !skipped.Contains(route) && !visited.Any(address => Matches(route, address))).ToList();
        var unknownVisited = visited.Where(address => address != UnknownAddress && !routes.Any(route => Matches(route, address))).ToList();
        var unknownSkipped = skipped.Where(route => !routes.Contains(route)).ToList();

        Assert.Empty(unswept);
        Assert.Empty(unknownVisited);
        Assert.Empty(unknownSkipped);
        Assert.Contains(UnknownAddress, visited);
    }

    private const string NoCallerYet = "no caller yet";

    [Fact]
    public void Every_dialog_the_app_opens_is_swept_or_excepted_with_a_reason_and_no_exception_is_stale()
    {
        var (swept, unswept) = SweepDialogs(SweepLib() + "\n" + string.Concat(SweepSpecs().Select(spec => spec.Text + "\n")));
        var (dialogs, called) = AppDialogs();

        Assert.DoesNotContain(dialogs, dialog => !swept.Contains(dialog) && !unswept.ContainsKey(dialog));

        Assert.DoesNotContain(swept, dialog => !dialogs.Contains(dialog));
        Assert.DoesNotContain(unswept.Keys, dialog => !dialogs.Contains(dialog) || swept.Contains(dialog));

        Assert.Empty(unswept.Where(entry => entry.Value.StartsWith(NoCallerYet, StringComparison.Ordinal) && called.Contains(entry.Key)).Select(entry => entry.Key));

        Assert.DoesNotContain(unswept.Keys, dialog => !Staffora.Test.Web.Common.ShortDialogAccessibilityTests.Covered.Contains(dialog)
            && !Staffora.Test.Integration.Web.StaffPagesTests.CheckedDialogs.Contains(dialog));
    }

    [Fact]
    public void The_dialog_scan_reads_the_swept_and_unswept_dialogs_and_the_apps_openers()
    {
        const string spec = """
            export const unsweptDialogs: Record<string, string> = {
              MessageDialog: 'no caller yet; swept when the first caller arrives',
            };
            await dialog('owner dialog: user', 'UserDialogComponent', fromPage('/x', (p) => p.click()));
            await dialog(`admin ${units}: edit`, 'NoteDialog', () => page.click(), { shotName: 'x' });
            """;
        const string extensions = """
            public static Task ShowMessageAsync(this DialogService dialogService) =>
                dialogService.OpenFormDialogAsync<MessageDialog, bool>(title);
            var result = await dialogService.OpenAsync<FormDialogHost>(title, hostParameters);
            await dialogService.OpenAsync<StateEntityDialogHost>(context.Title, hostParameters, options);
            """;
        const string page = """
            @* await DialogService.ShowMessageAsync("a comment is no caller"); *@
            var result = await DialogService.OpenAsync<UserDialogComponent>("User", parameters);
            await DialogService.OpenFormDialogAsync<NoteDialog>("Edit", options: null);
            dialogService.OpenStateEntityDialogAsync<TenantDialog, TenantDialogContext>(new TenantDialogContext(service, id));
            var session = new StateEntityDialogSession([StateEntityDialogTarget.For<JobDialog, JobDialogContext>(context)]);
            """;

        var (swept, unswept) = SweepDialogs(spec);
        var (dialogs, called) = ScanOpeners([("Staffora.Blazor/Helper/DialogServiceExtensions.cs", extensions), ("Staffora.Blazor/Pages/X.razor", page)]);

        Assert.Equal(["NoteDialog", "UserDialogComponent"], swept.Order(StringComparer.Ordinal));
        Assert.Equal(["MessageDialog"], unswept.Keys);
        Assert.Equal(["JobDialog", "MessageDialog", "NoteDialog", "TenantDialog", "UserDialogComponent"], dialogs.Order(StringComparer.Ordinal));
        Assert.Equal(["JobDialog", "NoteDialog", "TenantDialog", "UserDialogComponent"], called.Order(StringComparer.Ordinal));
    }

    private static (HashSet<string> Swept, Dictionary<string, string> Unswept) SweepDialogs(string spec)
    {
        var swept = SweptDialog().Matches(spec).Select(match => match.Groups["component"].Value).ToHashSet(StringComparer.Ordinal);
        var unswept = UnsweptList().Match(spec) is { Success: true } block
            ? UnsweptEntry().Matches(block.Groups["items"].Value).ToDictionary(entry => entry.Groups["name"].Value, entry => entry.Groups["reason"].Value, StringComparer.Ordinal)
            : [];
        return (swept, unswept);
    }

    private static (HashSet<string> Dialogs, HashSet<string> Called) AppDialogs() =>
        ScanOpeners(RepoFiles.CommittedTextFiles(".razor").Concat(RepoFiles.CommittedTextFiles(".cs"))
            .Select(file => (Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/'), File.ReadAllText(file)))
            .Where(file => file.Item1.StartsWith("Staffora.Blazor/", StringComparison.Ordinal)));

    private static (HashSet<string> Dialogs, HashSet<string> Called) ScanOpeners(IEnumerable<(string Path, string Text)> files)
    {
        var dialogs = new HashSet<string>(StringComparer.Ordinal);
        var called = new HashSet<string>(StringComparer.Ordinal);
        var byExtension = new Dictionary<string, string>(StringComparer.Ordinal);
        var bodies = new List<(string Path, string Text)>();
        foreach (var (path, text) in files)
        {
            var code = LineComment().Replace(BlockComment().Replace(Comment().Replace(text, string.Empty), string.Empty), string.Empty);
            bodies.Add((path, code));
            var isExtensions = path.EndsWith("/DialogServiceExtensions.cs", StringComparison.Ordinal);
            foreach (Match opener in Opener().Matches(code))
            {
                var dialog = opener.Groups["dialog"].Value;
                if (dialog is "FormDialogHost" or "StateEntityDialogHost" or "TComponent" or "TBody")
                {
                    continue;
                }

                dialogs.Add(dialog);
                if (!isExtensions)
                {
                    called.Add(dialog);
                }
            }

            if (isExtensions)
            {
                foreach (Match extension in ExtensionOpener().Matches(code).Where(extension => extension.Groups["dialog"].Value != "TComponent"))
                {
                    byExtension[extension.Groups["method"].Value] = extension.Groups["dialog"].Value;
                }
            }
        }

        foreach (var (path, code) in bodies.Where(body => !body.Path.EndsWith("/DialogServiceExtensions.cs", StringComparison.Ordinal)))
        {
            called.UnionWith(byExtension.Where(extension => Regex.IsMatch(code, $@"\.{extension.Key}\(")).Select(extension => extension.Value));
        }

        return (dialogs, called);
    }

    [GeneratedRegex(@"\bdialog\(\s*(?:'[^']*'|`[^`]*`)\s*,\s*'(?<component>\w+)'")]
    private static partial Regex SweptDialog();

    [GeneratedRegex(@"export const unsweptDialogs[^=]*=\s*\{(?<items>.*?)\};", RegexOptions.Singleline)]
    private static partial Regex UnsweptList();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*:\s*'(?<reason>(?:[^'\\]|\\.)*)'", RegexOptions.Multiline)]
    private static partial Regex UnsweptEntry();

    [GeneratedRegex(@"(?:\.(?:OpenAsync|OpenFormDialogAsync|OpenStateEntityDialogAsync)|\bStateEntityDialogTarget\.For)<(?<dialog>\w+)\s*[,>]")]
    private static partial Regex Opener();

    [GeneratedRegex(@"\b(?<method>\w+Async)\s*\(\s*this DialogService[^)]*\)[^;]*?OpenFormDialogAsync<(?<dialog>\w+)", RegexOptions.Singleline)]
    private static partial Regex ExtensionOpener();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"(?<!:)//[^\n]*")]
    private static partial Regex LineComment();

    private static bool Matches(string route, string address) =>
        Regex.IsMatch(address, "^" + Regex.Replace(Regex.Escape(route), @"\\\{\w+}", "[^/]+") + "$", RegexOptions.IgnoreCase);

    [Fact]
    public void The_scan_reads_the_page_lists_and_the_skipped_routes()
    {
        const string sample = """
            export const skippedRoutes: Record<string, string> = {
              '/a/{Id}': 'needs an id',
            };
            export const anonymousPages = ['/b', '/c'];
            export const ownerPages = [
              '/d',
            ];
            const other = ['/not-a-list'];
            """;

        var (visited, skipped) = SweepLists(sample);

        Assert.Equal(["/b", "/c", "/d"], visited.Order(StringComparer.Ordinal));
        Assert.Equal(["/a/{Id}"], skipped);
    }

    [Fact]
    public void Every_page_list_is_visited_by_a_part_of_the_sweep()
    {
        var lists = PageList().Matches(SweepLib()).Select(list => list.Groups["name"].Value).ToList();
        var specs = SweepSpecs();

        Assert.Equal(["sweep-staffing.spec.ts", "sweep-tenant.spec.ts", "sweep.spec.ts"], specs.Select(spec => spec.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["anonymousPages", "ownerPages", "portalPages", "staffPages", "tenantPages"], lists.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(lists, list => !specs.Any(spec => Regex.IsMatch(spec.Text, $@"\bfor\s*\(\s*const\s+\w+\s+of\s+{list}\s*\)\s*await\s+visit\(")));
    }

    private static string SweepLib() => File.ReadAllText(Path.Combine(RepoFiles.Root, "tests", "e2e", "lib", "sweep.ts"));

    private static List<(string Name, string Text)> SweepSpecs() =>
        [.. Directory.GetFiles(Path.Combine(RepoFiles.Root, "tests", "e2e"), "sweep*.spec.ts").Select(file => (Path.GetFileName(file), File.ReadAllText(file)))];

    private static (HashSet<string> Visited, HashSet<string> Skipped) SweepLists(string spec)
    {
        var visited = PageList().Matches(spec)
            .SelectMany(list => Quoted().Matches(list.Groups["items"].Value).Select(item => item.Groups["value"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var skipped = SkippedList().Match(spec) is { Success: true } block
            ? SkippedKey().Matches(block.Groups["items"].Value).Select(item => item.Groups["value"].Value).ToHashSet(StringComparer.Ordinal)
            : [];
        return (visited, skipped);
    }

    private static HashSet<string> Routes() =>
        RepoFiles.CommittedTextFiles(".razor")
            .Where(file => Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/').StartsWith("Staffora.Blazor/", StringComparison.Ordinal))
            .SelectMany(file => PageRoute().Matches(Comment().Replace(File.ReadAllText(file), string.Empty)).Select(match => match.Groups["route"].Value))
            .Select(route => Regex.Replace(route, @"\{(\w+)(:\w+)?\}", "{$1}"))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"export const (?<name>\w+Pages)\s*=\s*\[(?<items>[^\]]*)\]", RegexOptions.Singleline)]
    private static partial Regex PageList();

    [GeneratedRegex(@"export const skippedRoutes[^=]*=\s*\{(?<items>.*?)\};", RegexOptions.Singleline)]
    private static partial Regex SkippedList();

    [GeneratedRegex(@"'(?<value>/[^']*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\s*'(?<value>/[^']*)'\s*:", RegexOptions.Multiline)]
    private static partial Regex SkippedKey();

    [GeneratedRegex(@"^\s*@page\s+""(?<route>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex PageRoute();

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
