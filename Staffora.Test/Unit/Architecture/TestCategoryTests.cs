using System.Reflection;

namespace Staffora.Test.Unit.Architecture;

public class TestCategoryTests
{
    [Theory]
    [InlineData("Staffora.Test.Integration", "Integration")]
    [InlineData("Staffora.Test.E2E", "E2E")]
    public void Every_test_class_in_a_category_namespace_carries_the_category_trait(string ns, string category)
    {
        var missing = TestClassesIn(ns)
            .Where(type => !type.GetCustomAttributes<TraitAttribute>(inherit: true)
                .Any(trait => trait.Name == "Category" && trait.Value == category))
            .Select(type => type.FullName);

        Assert.Empty(missing);
    }

    [Fact]
    public void The_category_scan_finds_the_integration_classes()
    {
        Assert.Contains(typeof(Integration.TraitFilterTests), TestClassesIn("Staffora.Test.Integration"));
    }

    [Fact]
    public void The_category_scan_finds_the_end_to_end_platform_classes()
    {
        var classes = TestClassesIn("Staffora.Test.E2E").ToList();

        Assert.Contains(typeof(E2E.FoundationPrepTests), classes);
        Assert.Contains(typeof(E2E.StartupSeedGateTests), classes);
        Assert.Contains(typeof(E2E.HealthEndpointTests), classes);
    }

    private static IEnumerable<Type> TestClassesIn(string ns) =>
        typeof(TestCategoryTests).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.Namespace == ns || (type.Namespace?.StartsWith(ns + ".", StringComparison.Ordinal) ?? false))
            .Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any()));
}
