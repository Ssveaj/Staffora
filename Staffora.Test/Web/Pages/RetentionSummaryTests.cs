using Bunit;
using Staffora.Blazor.Components.TenantComponents;
using Staffora.Services.Platform;

namespace Staffora.Test.Web.Pages;

public class RetentionSummaryTests : WebTestContext
{
    private static readonly DateTimeOffset Finished = new(2027, 3, 10, 8, 5, 0, TimeSpan.Zero);

    [Fact]
    public void The_health_page_shows_when_the_pass_last_ran_and_what_each_rule_removed()
    {
        var summary = Render<RetentionSummary>(parameters => parameters
            .Add(component => component.Pass, new RetentionPass(Finished.AddSeconds(-4), Finished, 1240, 37, 5, 1, 2318, 0))
            .Add(component => component.Options, new RetentionOptions()));

        Assert.Equal("Retention", summary.Find("h2").TextContent.Trim());
        Assert.Equal("Last pass on 10 March 2027 at 08:05 UTC.", summary.Find("[data-retention-when]").TextContent.Trim());
        Assert.Equal(
            [("Error log entries removed", "1,240"), ("Read notices removed", "37"), ("Delivered messages removed", "5"), ("Tenants purged", "1 (2,318 rows)")],
            summary.FindAll(".stat-tile").Select(tile => (tile.QuerySelector(".stat-tile__label")!.TextContent.Trim(), tile.QuerySelector(".stat-tile__value")!.TextContent.Trim())));
        Assert.Contains("removes error-log entries after 90 days, read notices after 180 days and delivered integration messages after 30 days", summary.Markup, StringComparison.Ordinal);
        Assert.Contains("purged 30 days later", summary.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(";", summary.Find(".retention-summary").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pass_with_failures_points_to_the_error_log_and_no_pass_says_so()
    {
        var failed = Render<RetentionSummary>(parameters => parameters
            .Add(component => component.Pass, new RetentionPass(Finished, Finished, 0, 0, 0, 0, 0, 2))
            .Add(component => component.Options, new RetentionOptions { Interval = TimeSpan.FromMinutes(30) }));

        Assert.Equal("Last pass on 10 March 2027 at 08:05 UTC. 2 removals could not finish, and the error log holds their reasons.", failed.Find("[data-retention-when]").TextContent.Trim());
        Assert.Contains("Every 30 minutes the platform removes", failed.Markup, StringComparison.Ordinal);
        Assert.Equal("0", failed.FindAll(".stat-tile__value")[^1].TextContent.Trim());

        var none = Render<RetentionSummary>(parameters => parameters.Add(component => component.Options, new RetentionOptions()));

        Assert.Equal("The retention pass has not run since the app started.", none.Find("[data-retention-when]").TextContent.Trim());
        Assert.Empty(none.FindAll(".stat-tile"));
    }
}
