using System.Xml.Linq;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Architecture;

public class LayeringTests
{
    private static readonly Dictionary<string, string[]> allowedReferences = new()
    {
        ["Staffora.Core"] = [],
        ["Staffora.Data"] = ["Staffora.Core"],
        ["Staffora.Services"] = ["Staffora.Core", "Staffora.Data"],
        ["Staffora.Blazor"] = ["Staffora.Core", "Staffora.Data", "Staffora.Services"],

        ["Staffora.E2ESetup"] = ["Staffora.Core", "Staffora.Data", "Staffora.Services"],
    };

    [Theory]
    [InlineData("Staffora.Core")]
    [InlineData("Staffora.Data")]
    [InlineData("Staffora.Services")]
    [InlineData("Staffora.Blazor")]
    [InlineData("Staffora.E2ESetup")]
    public void Project_references_only_lower_layers(string project)
    {
        var referenced = ProjectReferences(project);

        var disallowed = referenced.Except(allowedReferences[project]).ToList();

        Assert.Empty(disallowed);
    }

    [Fact]
    public void Every_production_project_is_covered_by_the_layer_rules()
    {
        var productionProjects = Directory.EnumerateFiles(RepoFiles.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !RepoFiles.IsLocalOnly(Path.GetRelativePath(RepoFiles.Root, path)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && name.StartsWith("Staffora.", StringComparison.Ordinal) && name != "Staffora.Test")
            .Select(name => name!)
            .ToList();

        Assert.All(productionProjects, name => Assert.Contains(name, allowedReferences.Keys));
    }

    private static IEnumerable<string> ProjectReferences(string project)
    {
        var path = Path.Combine(RepoFiles.Root, project, $"{project}.csproj");
        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')));
    }
}
