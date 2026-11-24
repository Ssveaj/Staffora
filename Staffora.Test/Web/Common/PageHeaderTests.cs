using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Staffora.Blazor.Components.Common.Layout;
using Staffora.Blazor.Layout;

namespace Staffora.Test.Web.Common;

public class PageHeaderTests : WebTestContext
{
    [Fact]
    public void The_description_is_inside_the_help_popover_of_a_button_named_About_this_page_in_the_top_row()
    {
        var page = WithShellOutlets(new() { [nameof(PageHeader.Title)] = "Users", [nameof(PageHeader.Description)] = "Find the company's user accounts and open one to see it in full." });

        var crumb = page.Find("[data-crumb]");
        var button = crumb.QuerySelector("button[data-page-help-toggle]")!;
        Assert.Equal("About this page", button.GetAttribute("aria-label"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Equal(PageHeader.HelpId, button.GetAttribute("aria-controls"));
        Assert.Equal("true", button.QuerySelector(".material-symbol")?.GetAttribute("aria-hidden"));

        var popover = page.Find($"#{PageHeader.HelpId}");
        Assert.True(crumb.Contains(popover), "the help stands in the breadcrumb's last step, after the title");
        Assert.Equal("Find the company's user accounts and open one to see it in full.", popover.TextContent);
        Assert.True(popover.HasAttribute("hidden"), "the popover is closed until the reader opens it");

        Assert.Single(page.FindAll("*"), element => element.Children.Length == 0 && element.TextContent.Contains("Find the company's", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_description_there_is_no_help_button()
    {
        var page = WithShellOutlets(new() { [nameof(PageHeader.Title)] = "Users" });

        Assert.Empty(page.FindAll("[data-page-help-toggle]"));
        Assert.Empty(page.FindAll($"#{PageHeader.HelpId}"));
    }

    [Fact]
    public void The_title_is_the_one_h1_in_the_breadcrumbs_last_step_and_no_band_or_trail_is_drawn()
    {
        var page = WithShellOutlets(new()
        {
            [nameof(PageHeader.Title)] = "Users",
            [nameof(PageHeader.RootText)] = "Administration",
            [nameof(PageHeader.RootPath)] = "/tenant/management",
            [nameof(PageHeader.Count)] = 7,
        });

        var h1 = Assert.Single(page.FindAll("h1"));
        Assert.Equal("Users", h1.TextContent);
        Assert.True(page.Find("[data-crumb]").Contains(h1));
        Assert.Equal("7", page.Find("[data-crumb] .page-header__count").TextContent);
        Assert.Empty(page.FindAll("header"));
        Assert.Empty(page.FindAll("nav"));
        Assert.DoesNotContain("Administration", page.Markup);
    }

    [Fact]
    public void Without_a_count_none_is_shown()
    {
        var page = WithShellOutlets(new() { [nameof(PageHeader.Title)] = "Settings" });

        Assert.Empty(page.FindAll(".page-header__count"));
    }

    [Fact]
    public void The_actions_stand_in_the_page_at_the_end_of_its_first_row_in_their_order_after_the_meta()
    {
        RenderFragment actions = builder =>
        {
            builder.AddMarkupContent(0, "<button type=\"button\" id=\"first\">New user</button><button type=\"button\" data-step=\"second\">Refresh</button>");
        };
        RenderFragment meta = builder => builder.AddMarkupContent(0, "<span>State: Active</span>");

        var page = WithShellOutlets(new() { [nameof(PageHeader.Title)] = "Users", [nameof(PageHeader.Actions)] = actions, [nameof(PageHeader.Meta)] = meta });

        var lead = page.Find("[data-page] .page-header > .page-header__lead");
        Assert.Equal(["page-header__meta", "page-header__actions"], lead.Children.Select(child => child.ClassName));
        var buttons = lead.QuerySelectorAll(".page-header__actions button");
        Assert.Equal(["New user", "Refresh"], buttons.Select(button => button.TextContent));
        Assert.Equal("first", buttons[0].GetAttribute("id"));
        Assert.Equal("second", buttons[1].GetAttribute("data-step"));
        Assert.Contains("State: Active", lead.QuerySelector(".page-header__meta")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(page.Find("[data-crumb]").QuerySelectorAll("button:not([data-page-help-toggle])"));
    }

    [Fact]
    public void A_page_without_actions_meta_or_filters_draws_nothing_in_the_page()
    {
        var page = WithShellOutlets(new() { [nameof(PageHeader.Title)] = "Settings" });

        Assert.Empty(page.Find("[data-page]").Children);
    }

    private IRenderedComponent<IComponent> WithShellOutlets(Dictionary<string, object?> parameters) => Render(builder =>
    {
        builder.OpenElement(0, "li");
        builder.AddAttribute(1, "data-crumb", true);
        builder.OpenComponent<SectionOutlet>(2);
        builder.AddComponentParameter(3, nameof(SectionOutlet.SectionName), AppBreadcrumb.PageSection);
        builder.CloseComponent();
        builder.CloseElement();

        builder.OpenElement(8, "div");
        builder.AddAttribute(9, "data-page", true);
        builder.OpenComponent<PageHeader>(10);
        builder.AddMultipleAttributes(11, parameters!);
        builder.CloseComponent();
        builder.CloseElement();
    });
}
