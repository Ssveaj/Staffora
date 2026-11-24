using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Core.Models;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

public class StateHistoryDialogTests : WebTestContext
{
    private static readonly DateTime stamped = new(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly IRenderedComponent<RadzenDialog> host;
    private readonly DialogService dialogs;

    public StateHistoryDialogTests()
    {
        this.host = Render<RadzenDialog>();
        this.dialogs = Services.GetRequiredService<DialogService>();
    }

    [Fact]
    public async Task Pages_are_asked_for_by_a_steps_place_and_numbered_by_the_steps_newer_than_them()
    {
        var history = Enumerable.Range(0, 25).Select(Step).ToList();
        var asked = new List<StateHistoryQuery>();
        await OpenAsync(query =>
        {
            asked.Add(query);
            return Task.FromResult(Result<StateHistoryPage>.Success(PageOf(history, query)));
        });

        this.host.WaitForAssertion(() => Assert.Contains("Steps 1 to 20 of 25 · newest first", Summary(), StringComparison.Ordinal));
        Assert.Equal(StateHistoryQuery.Newest(StateHistoryDialog.PageSize), asked[0]);

        Pager("Older steps").Click();
        this.host.WaitForAssertion(() => Assert.Contains("Steps 21 to 25 of 25 · newest first", Summary(), StringComparison.Ordinal));
        Assert.Equal(new StateHistoryQuery(StateHistoryDialog.PageSize, OlderThan: history[19].Cursor), asked[1]);
        Assert.Equal("21", this.host.Find(".state-history__steps").GetAttribute("start"));
        Assert.True(Pager("Older steps").HasAttribute("disabled"));

        Pager("Newer steps").Click();
        this.host.WaitForAssertion(() => Assert.Contains("Steps 1 to 20 of 25 · newest first", Summary(), StringComparison.Ordinal));
        Assert.Equal(new StateHistoryQuery(StateHistoryDialog.PageSize, NewerThan: history[20].Cursor), asked[2]);
    }

    [Theory]
    [InlineData(true, "1 step · newest first · times in America/New_York")]
    [InlineData(false, "1 step · newest first · times in America/New_York, the zone of this record's organization")]
    public async Task The_summary_names_the_zone_the_times_are_in_and_whose_it_is(bool inReadersZone, string said)
    {
        await OpenAsync(_ => Task.FromResult(Result<StateHistoryPage>.Success(new StateHistoryPage([Step(0)], 1, 0, "America/New_York", inReadersZone))));

        this.host.WaitForAssertion(() => Assert.Equal(said, Summary()));
        Assert.Equal("America/New_York", this.host.Find("[data-history-zone]").TextContent);
    }

    [Fact]
    public async Task A_page_with_no_steps_never_reads_a_range_that_ends_before_it_starts()
    {
        await OpenAsync(_ => Task.FromResult(Result<StateHistoryPage>.Success(new StateHistoryPage([], 20, 20, "Europe/Stockholm"))));

        this.host.WaitForAssertion(() => Assert.Contains("No steps on this page. The history holds 20 steps.", this.host.Find("[data-state-history]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("21 to 20", this.host.Markup, StringComparison.Ordinal);
        Assert.Contains(this.host.FindAll("[data-state-history] button"), button => button.TextContent.Contains("Newest steps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_that_throws_shows_a_plain_alert_with_try_again_and_never_the_exceptions_text()
    {
        var throws = true;
        await OpenAsync(query => throws
            ? throw new InvalidOperationException("secret: the history's query broke")
            : Task.FromResult(Result<StateHistoryPage>.Success(PageOf([Step(0)], query))));

        this.host.WaitForAssertion(() => Assert.Contains(StateHistoryDialog.ReadFailed, this.host.Find("[data-state-history] [role=alert]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("secret", this.host.Markup, StringComparison.Ordinal);

        throws = false;
        this.host.FindAll("[data-state-history] button").Single(button => button.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();
        this.host.WaitForAssertion(() => Assert.Contains("1 step · newest first", Summary(), StringComparison.Ordinal));
        Assert.Empty(this.host.FindAll("[data-state-history] [role=alert]"));
    }

    [Fact]
    public async Task Steps_are_a_timeline_grouped_under_their_day_with_today_and_yesterday_named_and_the_time_alone_on_each_step()
    {
        var today = new DateOnly(2026, 7, 1);
        StateHistoryStep At(int month, int day, int hour, int minute, Enum from, Enum to) => Step(0) with
        {
            Id = Guid.NewGuid(),
            From = from,
            To = to,
            At = new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(2)),
        };
        StateHistoryStep[] steps =
        [
            At(7, 1, 19, 47, TestParentState.Draft, TestParentState.Active),
            At(7, 1, 9, 5, TestParentState.Draft, TestParentState.Draft),
            At(6, 30, 16, 30, TestParentState.Canceled, TestParentState.Draft),
            At(6, 28, 8, 0, TestParentState.Draft, TestParentState.Canceled),
        ];
        await OpenAsync(_ => Task.FromResult(Result<StateHistoryPage>.Success(new StateHistoryPage(steps, 4, 0, "Europe/Stockholm", Today: today))));

        this.host.WaitForAssertion(() => Assert.Equal("4 steps · newest first · times in Europe/Stockholm", Summary()));
        var items = this.host.FindAll(".state-history__steps > li");
        Assert.Equal(["Today", "", "Yesterday", "28 Jun 2026"], items.Select(item => item.QuerySelector("h3.state-history__day")?.TextContent ?? string.Empty));
        Assert.Equal(["19:47", "09:05", "16:30", "08:00"], items.Select(item => item.QuerySelector("time")!.TextContent));
        Assert.Equal([true, false, false, false], items.Select(item => item.QuerySelector(".history-step")!.ClassList.Contains("history-step--linked")));
    }

    [Fact]
    public async Task A_step_that_kept_its_state_shows_no_chips_and_its_marker_says_the_state_while_a_change_reads_from_and_to()
    {
        var kept = Step(0) with { Notices = [new StateHistoryNotice("Question about your absence", StateHistoryService.InAppChannel, Staffora.Core.Entities.Messaging.OutgoingMessageStatus.Delivered, 1)], ActorNotes = [StateHistoryService.ImpersonatedNote] };
        var changed = Step(1) with { Id = Guid.NewGuid(), From = TestParentState.Draft, To = TestParentState.Active };
        await OpenAsync(_ => Task.FromResult(Result<StateHistoryPage>.Success(new StateHistoryPage([kept, changed], 2, 0, "Europe/Stockholm"))));

        this.host.WaitForAssertion(() => Assert.Equal(2, this.host.FindAll("[data-history-step]").Count));
        var (first, second) = (this.host.FindAll("[data-history-step]")[0], this.host.FindAll("[data-history-step]")[1]);
        Assert.Null(first.QuerySelector(".history-step__states"));
        var marker = first.QuerySelector(".history-step__marker")!;
        Assert.Equal(("img", "Stayed in Draft"), (marker.GetAttribute("role"), marker.GetAttribute("aria-label")));
        Assert.Equal("Taken by a platform owner signed in as this user.", Spoken(first.QuerySelector(".history-step__note")!));
        var notice = first.QuerySelector("[data-history-notice=Delivered]")!;
        Assert.Equal("Notice: Question about your absence · delivered to 1 person", Spoken(notice));

        Assert.Equal("true", second.QuerySelector(".history-step__marker")!.GetAttribute("aria-hidden"));
        Assert.Equal("from Draft to Active", Spoken(second.QuerySelector(".history-step__states")!));
    }

    private async Task OpenAsync(Func<StateHistoryQuery, Task<Result<StateHistoryPage>>> load)
    {
        await this.host.InvokeAsync(() =>
        {
            _ = this.dialogs.OpenFormDialogAsync<StateHistoryDialog>("History: Parent", parameters => parameters.Set(dialog => dialog.LoadPage, load));
        });
        this.host.WaitForElement("[data-state-history]");
    }

    private static string Spoken(AngleSharp.Dom.INode node)
    {
        if (node is AngleSharp.Dom.IElement element && element.GetAttribute("aria-hidden") == "true")
        {
            return string.Empty;
        }

        var text = node is AngleSharp.Dom.IText words ? words.Data : string.Join(' ', node.ChildNodes.Select(Spoken));
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private string Summary() => this.host.Find(".state-history__summary").TextContent;

    private AngleSharp.Dom.IElement Pager(string words) =>
        this.host.FindAll(".state-history__pager button").Single(button => button.TextContent.Contains(words, StringComparison.Ordinal));

    private static StateHistoryStep Step(int index)
    {
        var stamped = StateHistoryDialogTests.stamped.AddMinutes(-index);
        return new StateHistoryStep(Guid.NewGuid(), "test-parent-save", "Save", TestParentState.Draft, TestParentState.Draft, new DateTimeOffset(stamped).ToOffset(TimeSpan.FromHours(2)), "Owner Company A", [], new StateHistoryCursor(stamped, Guid.NewGuid()));
    }

    private static StateHistoryPage PageOf(IReadOnlyList<StateHistoryStep> history, StateHistoryQuery query)
    {
        var start = query switch
        {
            { OlderThan: { } older } => history.ToList().FindIndex(step => step.Cursor == older) + 1,
            { NewerThan: { } newer } => Math.Max(0, history.ToList().FindIndex(step => step.Cursor == newer) - query.Take),
            _ => 0,
        };
        return new StateHistoryPage([.. history.Skip(start).Take(query.Take)], history.Count, start, "Europe/Stockholm");
    }
}
