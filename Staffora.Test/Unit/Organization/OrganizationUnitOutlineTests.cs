#pragma warning disable xUnit1069

using Staffora.Core.Entities.Organization;
using Staffora.Core.Models;

namespace Staffora.Test.Unit.Organization;

public class OrganizationUnitOutlineTests
{
    private static readonly Guid root = Guid.NewGuid();
    private static readonly Guid north = Guid.NewGuid();
    private static readonly Guid sales = Guid.NewGuid();
    private static readonly Guid south = Guid.NewGuid();

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task Units_come_in_tree_order_with_their_paths_and_units_out_of_use_are_marked()
    {
        var west = Guid.NewGuid();
        OrganizationUnitNode[] units =
        [
            new(west, root, "West", null, OrganizationUnitType.Region, OrganizationUnitState.Archived),
            new(south, root, "South", null, OrganizationUnitType.Region, OrganizationUnitState.Inactive),
            new(sales, north, "Sales", null, OrganizationUnitType.Branch, OrganizationUnitState.Active),
            new(north, root, "North", null, OrganizationUnitType.Region, OrganizationUnitState.Active),
            new(root, null, "Company A", null, OrganizationUnitType.Company, OrganizationUnitState.Active),
        ];

        var outline = OrganizationUnitOutline.Of(units);

        Assert.Equal(["Company A", "Company A › North", "Company A › North › Sales", "Company A › South", "Company A › West"], outline.Select(entry => entry.Path));
        Assert.Equal([0, 1, 2, 1, 1], outline.Select(entry => entry.Depth));
        Assert.Equal("Company A › South (inactive)", outline[3].Label);
        Assert.Equal("Company A › West (archived)", outline[4].Label);
        Assert.Equal("Company A › North", outline[1].Label);
        Assert.All(outline, entry => Assert.True(entry.IsLinked));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_stored_cycle_is_listed_after_the_tree_once_each_and_the_walk_returns()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        OrganizationUnitNode[] units =
        [
            new(root, null, "Company A", null, OrganizationUnitType.Company, OrganizationUnitState.Active),
            new(x, y, "X", null, OrganizationUnitType.Region, OrganizationUnitState.Active),
            new(y, x, "Y", null, OrganizationUnitType.Region, OrganizationUnitState.Active),
            new(sales, x, "Below the cycle", null, OrganizationUnitType.Branch, OrganizationUnitState.Active),
        ];

        var outline = OrganizationUnitOutline.Of(units);

        Assert.Equal([("Company A", true), ("Below the cycle", false), ("X", false), ("Y", false)], outline.Select(entry => (entry.Path, entry.IsLinked)));

        return Task.CompletedTask;
    }
}
