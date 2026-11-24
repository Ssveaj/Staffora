using System.Text.RegularExpressions;
using Bunit;
using Staffora.Blazor.Components.Common.Layout;
using Staffora.Blazor.Components.Common.Ui;

namespace Staffora.Test.Web.Common;

public class HomePartsTests : WebTestContext
{
    [Fact]
    public void The_head_shows_the_day_and_the_time_it_was_read_and_its_refresh_reads_again()
    {
        var refreshed = 0;
        var head = Render<HomeHead>(parameters => parameters
            .Add(component => component.Eyebrow, "Company A")
            .Add(component => component.Title, "Administration")
            .Add(component => component.Description, "How far the company is set up.")
            .Add(component => component.Now, new DateTimeOffset(2026, 11, 9, 10, 5, 0, TimeSpan.FromHours(1)))
            .Add(component => component.OnRefresh, () => refreshed++));

        Assert.Equal("Administration", Assert.Single(head.FindAll("h1")).TextContent.Trim());
        Assert.Equal("Company A", head.Find(".home-head__eyebrow").TextContent.Trim());
        Assert.Equal("Monday 9 November", head.Find(".home-head__date").TextContent.Trim());
        Assert.Equal("Updated 10:05", head.Find(".home-head__updated").TextContent.Trim());
        Assert.Equal("About this page", head.Find("button[data-page-help-toggle]").GetAttribute("aria-label"));
        Assert.True(head.Find($"#{PageHeader.HelpId}").HasAttribute("hidden"));

        var refresh = head.Find("[data-home-refresh]");
        Assert.Equal("Refresh", refresh.GetAttribute("aria-label"));
        refresh.Click();
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public void Without_the_time_the_head_shows_no_day_and_no_refresh()
    {
        var head = Render<HomeHead>(parameters => parameters.Add(component => component.Title, "My work"));

        Assert.Empty(head.FindAll(".home-head__meta"));
        Assert.Empty(head.FindAll("[data-home-refresh]"));
        Assert.Empty(head.FindAll("[data-page-help-toggle]"));
    }

    [Fact]
    public void A_figure_reads_as_its_value_and_label_and_a_linked_one_is_a_link()
    {
        var lead = Render<HomeFigure>(parameters => parameters
            .Add(component => component.Value, "3")
            .Add(component => component.Label, "open requests")
            .Add(component => component.Lead, true));

        Assert.Equal("3 open requests", Collapsed(lead.Find(".home-figure--lead").TextContent));

        var linked = Render<HomeFigure>(parameters => parameters
            .Add(component => component.Value, "0")
            .Add(component => component.Label, "Active")
            .Add(component => component.Zero, true)
            .Add(component => component.Href, "/superadmin/tenants?state=active"));

        var link = linked.Find("a.home-figure.home-figure--zero");
        Assert.Equal("/superadmin/tenants?state=active", link.GetAttribute("href"));
        Assert.Equal("0 Active", Collapsed(link.TextContent));
    }

    private static string Collapsed(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
