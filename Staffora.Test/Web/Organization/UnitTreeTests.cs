#pragma warning disable xUnit1069

using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Staffora.Blazor.Components.OrganizationComponents;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Models;

namespace Staffora.Test.Web.Organization;

public class UnitTreeTests : WebTestContext
{
    private static readonly Guid root = Guid.NewGuid();
    private static readonly Guid north = Guid.NewGuid();
    private static readonly Guid south = Guid.NewGuid();
    private static readonly Guid harbour = Guid.NewGuid();

    private static readonly OrganizationUnitNode[] units =
    [
        new(harbour, north, "Harbour office", null, OrganizationUnitType.Branch, OrganizationUnitState.Active),
        new(south, root, "South", null, OrganizationUnitType.Region, OrganizationUnitState.Active),
        new(root, null, "Company A", null, OrganizationUnitType.Company, OrganizationUnitState.Active),
        new(north, root, "North", null, OrganizationUnitType.Region, OrganizationUnitState.Active),
    ];

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task The_tree_renders_tree_items_with_their_levels_positions_and_groups()
    {
        var tree = Render<UnitTree>(parameters => parameters.Add(view => view.Units, units).Add(view => view.SelectedId, UnitTreeTests.root));

        var root = tree.Find("ul[role=tree]");
        Assert.Equal("Organization units", root.GetAttribute("aria-label"));
        var items = tree.FindAll("[role=treeitem]");
        Assert.Equal(["Company A", "North", "Harbour office", "South"], items.Select(item => item.QuerySelector(".unit-tree__label")!.TextContent));
        Assert.Equal(["1", "2", "3", "2"], items.Select(item => item.GetAttribute("aria-level")));
        Assert.Equal(["1", "2", "1", "2"], items.Select(item => item.GetAttribute("aria-setsize")));
        Assert.Equal(["1", "1", "1", "2"], items.Select(item => item.GetAttribute("aria-posinset")));
        Assert.Equal(["true", "true", null, null], items.Select(item => item.GetAttribute("aria-expanded")));
        Assert.Equal(["true", "false", "false", "false"], items.Select(item => item.GetAttribute("aria-selected")));

        Assert.All(items, item => Assert.Equal("none", item.ParentElement!.GetAttribute("role")));
        Assert.All(items, item => Assert.Empty(item.QuerySelectorAll("[role=treeitem], [role=group]")));
        Assert.All(items, item => Assert.False(item.HasAttribute("aria-labelledby") || item.HasAttribute("aria-label")));
        Assert.Equal(["Company A", "Company"], items[0].QuerySelectorAll(".unit-tree__label, .unit-tree__type").Select(text => text.TextContent));
        var owned = tree.Find($"#{items[0].GetAttribute("aria-owns")}");
        Assert.Equal("group", owned.GetAttribute("role"));
        Assert.Equal(["North", "Harbour office", "South"], owned.QuerySelectorAll("[role=treeitem] .unit-tree__label").Select(label => label.TextContent));
        Assert.Equal(["group"], items.Skip(1).Select(item => item.ParentElement!.ParentElement!.GetAttribute("role")).Distinct());
        Assert.False(items[2].HasAttribute("aria-owns"));

        Assert.Equal(["0", "-1", "-1", "-1"], items.Select(item => item.GetAttribute("tabindex")));

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task The_arrow_keys_move_the_tab_stop_and_open_and_close_items()
    {
        var tree = Render<UnitTree>(parameters => parameters.Add(view => view.Units, units));
        string TabStop() => tree.Find("[role=treeitem][tabindex='0'] .unit-tree__label").TextContent;
        void Press(string label, string key) => tree.FindAll("[role=treeitem]").Single(item => item.QuerySelector(".unit-tree__label")!.TextContent == label).KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Equal("Company A", TabStop());
        Press("Company A", "ArrowDown");
        Assert.Equal("North", TabStop());
        Press("North", "End");
        Assert.Equal("South", TabStop());
        Press("South", "Home");
        Assert.Equal("Company A", TabStop());

        Press("Company A", "ArrowRight");
        Assert.Equal("North", TabStop());
        Press("North", "ArrowLeft");
        Assert.Equal("false", tree.FindAll("[role=treeitem]")[1].GetAttribute("aria-expanded"));
        Assert.DoesNotContain(tree.FindAll(".unit-tree__label"), label => label.TextContent == "Harbour office");
        Press("North", "ArrowDown");
        Assert.Equal("South", TabStop());
        Press("South", "ArrowUp");
        Press("North", "ArrowRight");
        Assert.Equal("true", tree.FindAll("[role=treeitem]")[1].GetAttribute("aria-expanded"));
        Press("North", "ArrowLeft");
        Press("North", "ArrowLeft");
        Assert.Equal("Company A", TabStop());

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task Enter_Space_and_a_click_select_a_unit()
    {
        var selected = new List<string>();
        var tree = Render<UnitTree>(parameters => parameters
            .Add(view => view.Units, units)
            .Add(view => view.SelectedChanged, (OrganizationUnitNode unit) => selected.Add(unit.Name)));

        tree.FindAll("[role=treeitem]")[1].KeyDown(new KeyboardEventArgs { Key = "Enter" });
        tree.FindAll("[role=treeitem]")[3].KeyDown(new KeyboardEventArgs { Key = " " });
        tree.FindAll("[role=treeitem]")[2].Click();

        Assert.Equal(["North", "South", "Harbour office"], selected);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_stored_cycle_neither_hangs_the_tree_nor_loses_its_units()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var z = Guid.NewGuid();
        OrganizationUnitNode[] units =
        [
            new(root, null, "Company A", null, OrganizationUnitType.Company, OrganizationUnitState.Active),
            new(x, y, "X", null, OrganizationUnitType.Region, OrganizationUnitState.Active),
            new(y, x, "Y", null, OrganizationUnitType.Region, OrganizationUnitState.Active),
            new(z, x, "Z", null, OrganizationUnitType.Team, OrganizationUnitState.Active),
        ];

        var tree = Render<UnitTree>(parameters => parameters.Add(view => view.Units, units).Add(view => view.SelectedId, x));

        var items = tree.FindAll("[role=treeitem]");
        Assert.Equal(["Company A", "X", "Y", "Z"], items.Select(item => item.QuerySelector(".unit-tree__label")!.TextContent));
        Assert.Equal(["1", "1", "1", "1"], items.Select(item => item.GetAttribute("aria-level")));
        Assert.Equal(
            [false, true, true, true],
            items.Select(item => item.QuerySelectorAll(".unit-tree__flag").Any(flag => flag.TextContent == "(not linked to the tree)")));
        Assert.All(items, item => Assert.Null(item.GetAttribute("aria-expanded")));

        Assert.Equal(("true", "0"), (items[1].GetAttribute("aria-selected"), items[1].GetAttribute("tabindex")));
        tree.FindAll("[role=treeitem]")[1].KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        tree.FindAll("[role=treeitem]")[1].KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal("Y", tree.Find("[role=treeitem][tabindex='0'] .unit-tree__label").TextContent);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task An_inactive_unit_is_marked_in_its_row()
    {
        OrganizationUnitNode[] units =
        [
            new(root, null, "Company A", null, OrganizationUnitType.Company, OrganizationUnitState.Active),
            new(north, root, "North", null, OrganizationUnitType.Region, OrganizationUnitState.Inactive),
        ];

        var tree = Render<UnitTree>(parameters => parameters.Add(view => view.Units, units));

        var items = tree.FindAll("[role=treeitem]");
        Assert.Empty(items[0].QuerySelectorAll(".unit-tree__flag"));
        Assert.Equal("(inactive)", items[1].QuerySelector(".unit-tree__flag")!.TextContent);

        return Task.CompletedTask;
    }

    [Fact(Timeout = WalkTimeout.InMemory)]
    public Task A_tree_without_units_says_so_and_renders_no_tree_role()
    {
        var tree = Render<UnitTree>(parameters => parameters.Add(view => view.Units, Array.Empty<OrganizationUnitNode>()));

        Assert.Empty(tree.FindAll("[role=tree]"));
        Assert.Equal("No organization units yet.", tree.Find(".unit-tree__empty").TextContent);

        return Task.CompletedTask;
    }
}
