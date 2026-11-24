using System.Text.RegularExpressions;

namespace Staffora.Test.Docs;

public partial class DocsIntegrityTests
{
    private static readonly string repoRoot = FindRepoRoot();

    private static readonly string[] docRoots = ["docs", ".github", ".claude"];

    [Fact]
    public void Every_known_issue_reference_is_defined()
    {
        var defined = KnownIssueDefinition()
            .Matches(File.ReadAllText(Path.Combine(repoRoot, "docs", "known-issues.md")))
            .Select(m => m.Groups["id"].Value)
            .ToHashSet();

        var undefined = MarkdownFiles()
            .SelectMany(file => KnownIssueReference().Matches(File.ReadAllText(file))
                .Select(m => (File: Relative(file), Id: m.Value)))
            .Where(r => !defined.Contains(r.Id))
            .Select(r => $"{r.File}: {r.Id}")
            .Distinct()
            .ToList();

        Assert.Empty(undefined);
    }

    [Fact]
    public void Every_relative_markdown_link_resolves()
    {
        var broken = MarkdownFiles()
            .SelectMany(file => RelativeMarkdownLink().Matches(File.ReadAllText(file))
                .Select(m => (File: file, Target: m.Groups["target"].Value)))
            .Where(link => !File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.File)!, link.Target))))
            .Select(link => $"{Relative(link.File)} -> {link.Target}")
            .ToList();

        Assert.Empty(broken);
    }

    private static IEnumerable<string> MarkdownFiles() =>
        docRoots
            .Select(root => Path.Combine(repoRoot, root))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(repoRoot, "*.md", SearchOption.TopDirectoryOnly))
            .Where(file => !RepoFiles.IsLocalOnly(Relative(file)));

    private static string Relative(string path) => Path.GetRelativePath(repoRoot, path);

    private static string FindRepoRoot() => RepoFiles.Root;

    [GeneratedRegex(@"^\*\*(?<id>KI-\d+)", RegexOptions.Multiline)]
    private static partial Regex KnownIssueDefinition();

    [GeneratedRegex(@"KI-\d+")]
    private static partial Regex KnownIssueReference();

    [GeneratedRegex(@"\]\((?<target>(?!https?:)[^)#\s]+\.md)(#[^)]*)?\)")]
    private static partial Regex RelativeMarkdownLink();
}
