namespace Staffora.Test.Docs;

internal static class RepoFiles
{
    public static readonly string Root = FindRoot();

    private static readonly string[] localOnlyPrefixes = ["CLAUDE.local.md", ".claude/local/", ".claude/settings.local.json"];

    private static readonly string[] excludedDirectories = ["bin", "obj", ".vs", ".git", "node_modules", "TestResults", "test-results", "output", ".e2e"];

    public static bool IsLocalOnly(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return localOnlyPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> CommittedTextFiles(params string[] extensions) =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !IsInExcludedDirectory(file))
            .Where(file => !IsLocalOnly(Path.GetRelativePath(Root, file)));

    private static bool IsInExcludedDirectory(string file) =>
        Path.GetRelativePath(Root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1)
            .Any(segment => excludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Staffora.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Staffora.slnx not found above the test output directory.");
    }
}
