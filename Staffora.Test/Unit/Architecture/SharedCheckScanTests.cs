using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public class SharedCheckScanTests
{
    private static readonly Dictionary<string, string> noTenantCopies = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> deleteStepCopies = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> textCheckCopies = new(StringComparer.Ordinal);

    [Fact]
    public void Only_the_named_files_still_declare_the_no_tenant_refusal_of_their_own()
    {
        Assert.Equal(noTenantCopies.Keys.Order(StringComparer.Ordinal), ServiceFiles().Where(file => DeclaresNoTenant(file.Text)).Select(file => file.Path));
    }

    [Fact]
    public void Only_the_named_files_still_dispatch_a_delete_step_with_a_copy_of_their_own()
    {
        Assert.Equal(deleteStepCopies.Keys.Order(StringComparer.Ordinal), ServiceFiles().Where(file => CopiesDeleteStep(ServiceSources.Code(file.Text))).Select(file => file.Path));
    }

    [Fact]
    public void Only_the_named_files_still_judge_a_name_and_a_description_with_a_copy_of_their_own()
    {
        Assert.Equal(textCheckCopies.Keys.Order(StringComparer.Ordinal), ServiceFiles().Where(file => CopiesTextCheck(ServiceSources.Code(file.Text))).Select(file => file.Path));
    }

    [Fact]
    public void Every_named_copy_names_the_step_that_replaces_it()
    {
        Assert.All(noTenantCopies.Concat(deleteStepCopies).Concat(textCheckCopies), entry => Assert.Matches(@"^S\.\d+$", entry.Value));
    }

    [Fact]
    public void The_scan_catches_each_copy_and_passes_the_shared_forms()
    {
        Assert.True(DeclaresNoTenant("""private const string NoTenant = "No tenant is selected.";"""));
        Assert.False(DeclaresNoTenant("return Result.Failure(IRuntimeExecutionContext.NoTenant);"));

        Assert.True(CopiesDeleteStep(ServiceSources.Code("if (WorkflowRegistry.Default.Get<S, E>().TryGetTransition(transitionKey, out var step) && ((Enum)step.To).IsDeleteState())")));
        Assert.True(CopiesDeleteStep(ServiceSources.Code("if (workflow.TryGetTransition(key, out var found)\n    && ((Enum)found.To).IsDeleteState())")));
        Assert.False(CopiesDeleteStep(ServiceSources.Code("if (WorkflowRegistry.Default.Get<S, E>().IsDeleteStep(transitionKey))")));
        Assert.False(CopiesDeleteStep(ServiceSources.Code("// TryGetTransition(key, out var step) && ((Enum)step.To).IsDeleteState()")));

        Assert.True(CopiesTextCheck(ServiceSources.Code("private static string? DescribeInvalidText(string name, string? description)")));
        Assert.True(CopiesTextCheck(ServiceSources.Code("private static string? DescribeInvalidRoleText(string name, string? description)")));
        Assert.False(CopiesTextCheck(ServiceSources.Code("private static string? DescribeInvalidText(Job job) =>")));
        Assert.False(CopiesTextCheck(ServiceSources.Code("if (TextRule.Problem(name, description) is { } textReason)")));
    }

    private static bool DeclaresNoTenant(string text) => text.Contains("\"No tenant is selected.\"", StringComparison.Ordinal);

    private static bool CopiesDeleteStep(string code) =>
        Regex.IsMatch(code, @"TryGetTransition\([^;]*?\)\s*&&\s*\(\(Enum\)\s*\w+\.To\)\.IsDeleteState\(\)");

    private static bool CopiesTextCheck(string code) =>
        Regex.IsMatch(code, @"string\?\s+DescribeInvalid\w*Text\(\s*string\s+\w+\s*,\s*string\?\s+\w+\s*\)");

    private static IEnumerable<(string Path, string Text)> ServiceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, "Staffora.Services"), "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(path => (path, File.ReadAllText(Path.Combine(RepoFiles.Root, path))));
}
