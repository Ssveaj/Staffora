using System.Text.RegularExpressions;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public partial class OfferedServiceWriteScanTests
{
    private static readonly string[] projects = ["Staffora.Core", "Staffora.Data", "Staffora.Services", "Staffora.Blazor", "Staffora.E2ESetup"];

    [Fact]
    public void Only_the_service_message_handlers_write_a_portals_copies()
    {
        var writers = RepoFiles.CommittedTextFiles(".cs", ".razor")
            .Select(path => (Path: Path.GetRelativePath(RepoFiles.Root, path).Replace('\\', '/'), Text: File.ReadAllText(path)))
            .Where(file => projects.Any(project => file.Path.StartsWith(project + "/", StringComparison.Ordinal)))
            .Where(file => Writes().IsMatch(file.Text))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Staffora.Services/Portal/ServiceOfferHandlers.cs"], writers);
    }

    [Theory]
    [InlineData("context.OfferedServices.Add(copy);", true)]
    [InlineData("var copy = new OfferedService { Name = name };", true)]
    [InlineData("context.OfferedServices.AsTracking().FirstOrDefaultAsync(row => row.Id == id)", true)]
    [InlineData("await context.OfferedServices.AsNoTracking().ToListAsync();", false)]
    [InlineData("new OfferedServiceChoice(id, name, null, 1, true)", false)]
    public void The_scan_tells_a_write_from_a_read(string code, bool writes) => Assert.Equal(writes, Writes().IsMatch(code));

    [GeneratedRegex(@"new\s+OfferedService\b(?!Choice|Row)|OfferedServices\s*\.\s*(Add|AddRange|Remove|RemoveRange|Attach|Update|AsTracking|ExecuteUpdate|ExecuteDelete)\b")]
    private static partial Regex Writes();
}
