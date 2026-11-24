using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class SessionStateScanTests
{
    private static readonly string[] productionProjects = ["Staffora.Core", "Staffora.Data", "Staffora.Services", "Staffora.Blazor"];

    private static readonly Dictionary<string, string> pagesWithoutPolicy = new(StringComparer.Ordinal)
    {
        ["Staffora.Blazor/Pages/Home.razor"] = "the start page shows nothing; it sends a platform owner who is not impersonating to the platform console",
        ["Staffora.Blazor/Pages/NotFound.razor"] = "the not-found page, open to everyone",
        ["Staffora.Blazor/Pages/StatusPage.razor"] = "the page of an error status (unknown address, too many attempts), open to everyone, showing only the status",
        ["Staffora.Blazor/Pages/Error.razor"] = "the error page, open to everyone",
        ["Staffora.Blazor/Components/Account/Pages/AccessDenied.razor"] = "where every refused page sends the browser; it shows only the refusal and, while impersonating, the end button",
        ["Staffora.Blazor/Components/Account/Pages/Lockout.razor"] = "sign-in flow, open before anyone signs in",
        ["Staffora.Blazor/Components/Account/Pages/Login.razor"] = "the sign-in page itself",
    };

    [Fact]
    public void No_production_file_keeps_state_in_browser_storage()
    {
        var offenders = ProductionSources()
            .SelectMany(source => FindBrowserStorage(source.Text).Select(hit => $"{source.Path}: {hit}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_storage_scan_catches_every_kind_of_browser_storage_and_ignores_comments()
    {
        const string sample = """
            @inject ProtectedSessionStorage Session
            public class Snapshot(ProtectedLocalStorage local, Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage.ProtectedBrowserStorage any);
            await JS.InvokeVoidAsync("sessionStorage.setItem", key, value);
            window.localStorage.getItem(key);
            // ProtectedSessionStorage named in a comment is fine.
            @* so is localStorage in a Razor comment *@
            """;

        Assert.Equal(
            ["ProtectedSessionStorage", "ProtectedLocalStorage", "ProtectedBrowserStorage", "ProtectedBrowserStorage", "sessionStorage", "localStorage"],
            FindBrowserStorage(sample));
    }

    [Fact]
    public void Every_page_opens_by_a_session_policy_or_is_allow_listed()
    {
        var offenders = Pages()
            .Where(page => !pagesWithoutPolicy.ContainsKey(page.Path) && PolicyOf(page) is null)
            .Select(page => page.Path);

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_console_and_the_tenant_pages_name_their_own_policy()
    {
        var policies = Pages().ToDictionary(page => page.Path, PolicyOf, StringComparer.Ordinal);

        Assert.All(policies.Where(page => page.Key.StartsWith("Staffora.Blazor/Pages/SuperAdmin/", StringComparison.Ordinal)), page => Assert.Equal("PlatformConsole", page.Value));
        Assert.All(policies.Where(page => page.Key.StartsWith("Staffora.Blazor/Pages/TenantAdmin/", StringComparison.Ordinal) || page.Key.StartsWith("Staffora.Blazor/Pages/Me/", StringComparison.Ordinal)), page => Assert.Equal("TenantPages", page.Value));
        Assert.Contains(policies, page => page.Value == "PlatformConsole");
        Assert.Contains(policies, page => page.Value == "TenantPages");
    }

    [Fact]
    public void No_page_of_ones_own_account_exists()
    {
        var accountPages = Pages().Where(page => page.Path.StartsWith("Staffora.Blazor/Components/Account/", StringComparison.Ordinal)).Select(page => page.Path).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            ["Staffora.Blazor/Components/Account/Pages/AccessDenied.razor", "Staffora.Blazor/Components/Account/Pages/Lockout.razor", "Staffora.Blazor/Components/Account/Pages/Login.razor"],
            accountPages);
        Assert.All(Pages(), page => Assert.True(PolicyOf(page) is null or "PlatformConsole" or "TenantPages", $"{page.Path}: {PolicyOf(page)}"));
    }

    [Fact]
    public void The_policy_scan_reads_plain_and_qualified_authorize_attributes()
    {
        Assert.Equal("PlatformConsole", PolicyOf(("sample", "@attribute [Microsoft.AspNetCore.Authorization.Authorize(Policy = Staffora.Services.Authorization.SessionPolicies.PlatformConsole)]", string.Empty)));
        Assert.Equal("TenantPages", PolicyOf(("sample", string.Empty, "@attribute [Authorize(Policy = SessionPolicies.TenantPages)]")));
        Assert.Null(PolicyOf(("sample", "@attribute [Microsoft.AspNetCore.Authorization.Authorize]", string.Empty)));
    }

    [Fact]
    public void Every_page_without_a_policy_is_a_page_and_names_a_reason()
    {
        var pages = Pages().ToDictionary(page => page.Path, page => page, StringComparer.Ordinal);
        var stale = pagesWithoutPolicy
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !pages.TryGetValue(entry.Key, out var page) || PolicyOf(page) is not null)
            .Select(entry => entry.Key);

        Assert.Empty(stale);
    }

    [Fact]
    public void No_production_statement_reads_two_facts_of_the_runtime_one_by_one()
    {
        var offenders = ProductionSources()
            .Where(source => !source.Path.StartsWith("Staffora.Core/Execution/", StringComparison.Ordinal))
            .SelectMany(source => TornRuntimeReads(source.Text).Select(statement => $"{source.Path}: {statement}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_runtime_scan_catches_two_property_reads_in_one_statement_and_passes_a_snapshot()
    {
        const string sample = """
            if (!runtime.HasPlatformReach() && runtime.TenantId != tenantId) return;
            var writer = runtime.EffectiveUserId is { } actor && runtime.TenantId == user.TenantId ? actor : null;
            row.ImpersonatedById = RuntimeContext?.ImpersonatedById ?? Runtime.UserId;
            var session = runtime.Current;
            if (!session.HasPlatformReach() && session.TenantId != tenantId) return;
            var tenant = runtime.TenantId;
            // runtime.UserId and runtime.TenantId named in a comment are fine.
            """;

        Assert.Equal(3, TornRuntimeReads(sample).Count);
    }

    private static IEnumerable<(string Path, string Text, string Imports)> Pages()
    {
        var sources = SourcesOf(["Staffora.Blazor"], ".razor").ToDictionary(source => source.Path, source => source.Text, StringComparer.Ordinal);
        foreach (var (path, text) in sources.Where(source => PageDirective().IsMatch(WithoutComments(source.Value))))
        {
            var imports = new List<string>();
            for (var folder = Path.GetDirectoryName(path)!.Replace('\\', '/'); folder.Contains('/'); folder = Path.GetDirectoryName(folder)!.Replace('\\', '/'))
            {
                if (sources.TryGetValue($"{folder}/_Imports.razor", out var import))
                {
                    imports.Add(import);
                }
            }

            yield return (path, text, string.Join('\n', imports));
        }
    }

    private static string? PolicyOf((string Path, string Text, string Imports) page) =>
        PolicyAttribute().Match(WithoutComments(page.Text) + "\n" + WithoutComments(page.Imports)) is { Success: true } match ? match.Groups["policy"].Value : null;

    private static List<string> TornRuntimeReads(string source) =>
        [.. WithoutComments(source).Split(';').Where(statement => RuntimeFact().Matches(statement).Count >= 2).Select(statement => string.Join(' ', statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))];

    private static List<string> FindBrowserStorage(string source) =>
        [.. BrowserStorage().Matches(WithoutComments(source)).Select(match => match.Value)];

    private static IEnumerable<(string Path, string Text)> ProductionSources() =>
        SourcesOf(productionProjects, ".cs", ".razor", ".js")
            .Where(source => !source.Path.Contains("/wwwroot/lib/", StringComparison.Ordinal));

    private static IEnumerable<(string Path, string Text)> SourcesOf(IReadOnlyCollection<string> projects, params string[] extensions) =>
        RepoFiles.CommittedTextFiles(extensions)
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => projects.Any(project => source.Path.StartsWith(project + "/", StringComparison.Ordinal)))
            .Select(source => (source.Path, File.ReadAllText(source.File)));

    private static string WithoutComments(string source) => Comment().Replace(source, string.Empty);

    [GeneratedRegex(@"\b(?:Protected(?:Session|Local|Browser)Storage|sessionStorage|localStorage)\b")]
    private static partial Regex BrowserStorage();

    [GeneratedRegex(@"\b_?[Rr]untime(?:Context)?\??\.(?:TenantId|BaseTenantId|IsInPlatformTenant|ImpersonatedTenantId|UserId|ImpersonatedUserId|IsImpersonating|IsInitialized|IsSuperAdmin|EffectiveUserId|ImpersonatedById|HasPlatformReach\(\)|IsPlatformOwner\(\))")]
    private static partial Regex RuntimeFact();

    [GeneratedRegex(@"^\s*@page\b", RegexOptions.Multiline)]
    private static partial Regex PageDirective();

    [GeneratedRegex(@"\[(?:[\w.]+\.)?Authorize\(\s*Policy\s*=\s*(?:(?:[\w.]+\.)?SessionPolicies\.(?<policy>\w+)|""(?<policy>\w+)"")\s*\)\]")]
    private static partial Regex PolicyAttribute();

    [GeneratedRegex(@"(?<!:)//[^\n]*|/\*.*?\*/|@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
