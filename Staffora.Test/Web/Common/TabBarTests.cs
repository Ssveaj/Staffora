using Bunit;
using Staffora.Blazor.Components.Common.Ui;

namespace Staffora.Test.Web.Common;

public class TabBarTests : WebTestContext
{
    private static readonly TabBarItem[] items =
    [
        new("all", "All", 7),
        new("orders", "Orders", 2, "inbox"),
        new("slots", "Slots"),
    ];

    [Fact]
    public void The_tabs_follow_the_tab_pattern_with_one_tab_stop_on_the_selected_one()
    {
        var bar = Render<TabBar>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.Selected, "orders")
            .Add(component => component.Label, "Kinds of work")
            .Add(component => component.IdPrefix, "kinds")
            .Add(component => component.PanelId, "kinds-panel")
            .Add(component => component.ItemAttribute, "data-kind"));

        var list = bar.Find("[role=tablist]");
        Assert.Equal("Kinds of work", list.GetAttribute("aria-label"));
        var tabs = bar.FindAll("[role=tab]");
        Assert.Equal(["kinds-all", "kinds-orders", "kinds-slots"], tabs.Select(tab => tab.Id));
        Assert.Equal(["false", "true", "false"], tabs.Select(tab => tab.GetAttribute("aria-selected")));
        Assert.Equal(["-1", "0", "-1"], tabs.Select(tab => tab.GetAttribute("tabindex")));
        Assert.All(tabs, tab => Assert.Equal("kinds-panel", tab.GetAttribute("aria-controls")));
        Assert.Equal(["all", "orders", "slots"], tabs.Select(tab => tab.GetAttribute("data-kind")));
        Assert.Equal(["7", "2"], bar.FindAll(".tab-bar__count").Select(count => count.TextContent));
        Assert.Single(bar.FindAll(".tab-bar__tab--selected .tab-bar__icon"));
        Assert.Equal("kinds-orders", TabBar.TabId("kinds", "orders"));
    }

    [Fact]
    public void A_click_on_another_tab_names_its_key_and_the_selected_one_names_nothing()
    {
        var chosen = new List<string>();
        var bar = Render<TabBar>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.Selected, "all")
            .Add(component => component.Label, "Kinds of work")
            .Add(component => component.IdPrefix, "kinds")
            .Add(component => component.SelectedChanged, (string key) => chosen.Add(key)));

        bar.Find("#kinds-slots").Click();
        bar.Find("#kinds-all").Click();

        Assert.Equal(["slots"], chosen);
    }
}
