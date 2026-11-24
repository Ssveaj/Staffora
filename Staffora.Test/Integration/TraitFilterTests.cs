using System.Reflection;

namespace Staffora.Test.Integration;

[Trait("Category", "Integration")]
public class TraitFilterTests
{
    [Fact]
    public void The_integration_filter_selects_this_class()
    {
        var traits = typeof(TraitFilterTests).GetCustomAttributes<TraitAttribute>();

        Assert.Contains(traits, trait => trait.Name == "Category" && trait.Value == "Integration");
    }
}
