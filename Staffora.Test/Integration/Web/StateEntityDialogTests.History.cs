using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Core;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

public partial class StateEntityDialogTests
{
    [Fact]
    public async Task No_history_is_read_before_the_state_pills_card_first_opens_not_by_the_load_and_not_by_a_step()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);

        await MoveAsync(scenario, id, "test-parent-save", cancellationToken);
        await MoveAsync(scenario, id, "test-parent-save", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Equal("folder", dialog.Host.Find(".state-entity-dialog-header__icon").TextContent);
        Assert.Contains("Draft", dialog.Host.Find(".state-pill__button").TextContent, StringComparison.Ordinal);
        Assert.Equal((0, 0), (dialog.Context().HistoryRequests, circuit.Probe.HistoryReads));

        dialog.Step("test-parent-save").Click();
        dialog.WaitUntilAsked(2);
        Assert.Equal((0, 0), (dialog.Context().HistoryRequests, circuit.Probe.HistoryReads));

        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointermove", Mouse);
        dialog.Host.WaitForAssertion(() => Assert.Equal(3, Card(dialog).QuerySelectorAll("[data-history-step]").Length), Wait);
        Assert.Equal(1, dialog.Context().HistoryRequests);
        Assert.True(circuit.Probe.HistoryReads > 0);

        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointerleave", Mouse);
        dialog.Host.WaitForAssertion(() => Assert.True(Card(dialog).HasAttribute("hidden")), Wait);
        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointermove", Mouse);
        dialog.Host.WaitForAssertion(() => Assert.False(Card(dialog).HasAttribute("hidden")), Wait);
        Assert.Equal(1, dialog.Context().HistoryRequests);
    }

    [Fact]
    public async Task A_step_taken_in_the_footer_closes_the_card_and_appears_in_it_when_it_next_opens_without_reopening_the_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("No steps yet.", Card(dialog).TextContent, StringComparison.Ordinal), Wait);

        dialog.Step("test-parent-save").Click();
        dialog.WaitUntilAsked(2);
        Assert.True(Card(dialog).HasAttribute("hidden"));

        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointermove", Mouse);
        dialog.Host.WaitForAssertion(() => Assert.Equal(["Save"], StepNames(dialog)), Wait);
        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointerleave", Mouse);
        dialog.Step("test-parent-activate").Click();
        dialog.WaitUntilAsked(3);
        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointermove", Mouse);
        dialog.Host.WaitForAssertion(() => Assert.Equal(["Active", "Save"], StepNames(dialog)), Wait);
        Assert.Equal(3, context.HistoryRequests);

        Assert.Same(context, dialog.Context());
        Assert.Contains("Active", dialog.Host.Find(".state-pill__button").TextContent, StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);
        var step = Card(dialog).QuerySelector("[data-history-step]")!;
        Assert.Contains("Draft", step.QuerySelector(".history-step__states")!.TextContent, StringComparison.Ordinal);
        Assert.Contains($"Owner {scenario.A.Name}", step.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_version_reaches_the_open_card_at_once_and_an_answer_for_an_older_version_is_dropped()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        circuit.Service.AfterHistoryRead = async () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                heldRead.TrySetResult();
                await held.Task;
                heldReturned.TrySetResult();
            }
        };
        dialog.Host.Find(".state-pill__button").Click();
        await heldRead.Task.WaitAsync(Wait, cancellationToken);

        await MoveAsync(scenario, id, "test-parent-save", cancellationToken);
        await dialog.Host.InvokeAsync(() => dialog.Context().ReloadEntityAsync());
        dialog.Host.WaitForAssertion(() => Assert.Equal(["Save"], StepNames(dialog)), Wait);
        Assert.False(Card(dialog).HasAttribute("hidden"));

        held.SetResult();
        await heldReturned.Task.WaitAsync(Wait, cancellationToken);
        await dialog.Host.InvokeAsync(() => { });
        await dialog.Host.InvokeAsync(() => { });
        Assert.Equal(["Save"], StepNames(dialog));
        Assert.DoesNotContain("No steps yet.", Card(dialog).TextContent, StringComparison.Ordinal);
        Assert.Equal(2, dialog.Context().HistoryRequests);
    }

    [Fact]
    public async Task The_card_shows_the_last_three_steps_newest_first_and_the_full_history_dialog_pages_them_in_the_tenants_time_zone()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);

        await using (var mover = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            for (var step = 0; step < StateHistoryDialog.PageSize; step++)
            {
                Assert.True((await mover.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-save", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken)).IsSuccess);
            }

            Assert.True((await mover.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken)).IsSuccess);
        }

        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(["Active", "Save", "Save"], StepNames(dialog)), Wait);

        Card(dialog).QuerySelector("[data-open-history]")!.Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(StateHistoryDialog.PageSize, dialog.Host.FindAll(".state-history__steps > li").Count), Wait);
        Assert.Equal("History: Parent", dialog.Host.Find(".form-dialog-header__title").TextContent);
        var total = StateHistoryDialog.PageSize + 1;
        Assert.Equal($"Steps 1 to {StateHistoryDialog.PageSize} of {total} · newest first · times in {ServiceHost.PlatformTimeZone}", dialog.Host.Find(".state-history__summary").TextContent);
        Assert.Equal("Active", dialog.Host.FindAll("[data-state-history] .history-step__name")[0].TextContent);

        var newest = (await HistoryOfAsync(id, cancellationToken)).Single(entry => entry.TransitionKey == "test-parent-activate");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(ServiceHost.PlatformTimeZone);
        var local = TenantClock.ToLocal(newest.Created, zone);
        var time = dialog.Host.Find("[data-state-history] time");
        Assert.Equal(local.ToString("o", CultureInfo.InvariantCulture), time.GetAttribute("datetime"));
        Assert.Equal(local.ToString("HH:mm", CultureInfo.InvariantCulture), time.TextContent);
        var today = DateOnly.FromDateTime(TenantClock.ToLocal(DateTime.UtcNow, zone).DateTime);
        Assert.Equal(StateHistoryStepView.DayOf(DateOnly.FromDateTime(local.DateTime), today), dialog.Host.Find("[data-state-history] .state-history__day").TextContent);
        Assert.Contains(dialog.Host.Find("[data-state-history] .state-history__day").TextContent, new[] { "Today", "Yesterday" });

        var cardTime = Card(dialog).QuerySelector("time")!;
        Assert.EndsWith(local.ToString(" HH:mm", CultureInfo.InvariantCulture), cardTime.TextContent, StringComparison.Ordinal);
        Assert.StartsWith(StateHistoryStepView.DayOf(DateOnly.FromDateTime(local.DateTime), today), cardTime.TextContent, StringComparison.Ordinal);

        dialog.Host.FindAll(".state-history__pager button").Single(button => button.TextContent.Contains("Older steps", StringComparison.Ordinal)).Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains($"Steps {total} to {total} of {total}", dialog.Host.Find(".state-history__summary").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(["Save"], dialog.Host.FindAll("[data-state-history] .history-step__name").Select(name => name.TextContent));
    }

    [Fact]
    public async Task The_pill_is_a_disclosure_button_whose_card_Esc_closes_without_closing_the_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        var pill = dialog.Host.Find(".state-pill__button");
        Assert.Equal("button", pill.TagName.ToLowerInvariant());
        Assert.Equal("State: Draft", string.Concat(pill.QuerySelectorAll(".visually-hidden, .state-pill__name").Select(part => part.TextContent)));
        Assert.Equal("false", pill.GetAttribute("aria-expanded"));
        Assert.Equal(pill.GetAttribute("aria-controls"), Card(dialog).Id);
        Assert.True(Card(dialog).HasAttribute("hidden"));

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Host.Find(".state-pill__button").GetAttribute("aria-expanded")), Wait);
        Assert.Contains("Being prepared", Card(dialog).TextContent, StringComparison.Ordinal);
        Assert.Equal(["Full history"], Card(dialog).QuerySelectorAll(".state-pill__links button > span:last-child").Select(words => words.TextContent));

        var component = dialog.Host.FindComponent<StateEntityDialogStatePill>();
        await component.InvokeAsync(() => component.Instance.CloseCardByEscape());
        dialog.Host.WaitForAssertion(() => Assert.True(Card(dialog).HasAttribute("hidden")), Wait);
        Assert.Equal("false", dialog.Host.Find(".state-pill__button").GetAttribute("aria-expanded"));
        Assert.False(dialog.Result.IsCompleted);
        Assert.Single(dialog.Host.FindAll(".state-entity-dialog"));
    }

    [Fact]
    public async Task A_click_toggles_the_card_whatever_opened_it_a_tap_opens_it_once_and_only_a_card_opened_by_pointing_closes_as_the_pointer_leaves()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        string Expanded() => dialog.Host.Find(".state-pill__button").GetAttribute("aria-expanded")!;

        await Assert.ThrowsAsync<MissingEventHandlerException>(() => dialog.Host.Find(".state-pill").TriggerEventAsync("onpointerenter", Mouse));
        Assert.Equal("false", Expanded());

        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointermove", Mouse);
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", Expanded()), Wait);
        await Assert.ThrowsAsync<MissingEventHandlerException>(() => dialog.Host.Find(".state-pill").TriggerEventAsync("onpointermove", Mouse));
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", Expanded()), Wait);
        Assert.True(Card(dialog).HasAttribute("hidden"));

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", Expanded()), Wait);
        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointerleave", Mouse);
        Assert.Equal("true", Expanded());
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", Expanded()), Wait);

        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointermove", Touch);
        Assert.Equal("false", Expanded());
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", Expanded()), Wait);
        await dialog.Host.Find(".state-pill").TriggerEventAsync("onpointerleave", Touch);
        Assert.Equal("true", Expanded());
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task A_dialog_opened_on_top_closes_the_card_so_the_next_Esc_is_that_dialogs()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Host.Find(".state-pill").GetAttribute("data-open")), Wait);

        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForAssertion(() => Assert.NotEmpty(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", dialog.Host.Find(".state-pill").GetAttribute("data-open")), Wait);
        Assert.True(Card(dialog).HasAttribute("hidden"));

        dialog.ConfirmButton("Cancel").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Host.Find(".state-pill").GetAttribute("data-open")), Wait);
        var component = dialog.Host.FindComponent<StateEntityDialogStatePill>();
        await component.InvokeAsync(() => component.Instance.CloseCardFromOutside());
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", dialog.Host.Find(".state-pill").GetAttribute("data-open")), Wait);
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task The_focus_goes_back_to_the_pill_when_the_full_history_closes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var module = circuit.Page.JSInterop.SetupModule(StateEntityDialogHeader.ModulePath);
        var dialog = await circuit.OpenLoadedAsync(id);
        dialog.Host.WaitForAssertion(() => Assert.Single(module.Invocations["holdEscape"]), Wait);
        var pill = (ElementReference)module.Invocations["holdEscape"].Single().Arguments[1]!;

        var before = module.Invocations["returnFocus"].Count;
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.False(Card(dialog).HasAttribute("hidden")), Wait);
        Card(dialog).QuerySelector("[data-open-history]")!.Click();
        dialog.Host.WaitForAssertion(() => Assert.NotEmpty(dialog.Host.FindAll("[data-state-history]")), Wait);
        Assert.Equal(before, module.Invocations["returnFocus"].Count);

        await dialog.Host.InvokeAsync(() => circuit.Dialogs.Close());
        dialog.Host.WaitForAssertion(() => Assert.Equal(before + 1, module.Invocations["returnFocus"].Count), Wait);
        Assert.Equal(pill.Id, ((ElementReference)module.Invocations["returnFocus"].Last().Arguments[0]!).Id);
    }

    [Fact]
    public async Task A_full_history_page_that_throws_is_a_plain_alert_with_try_again_and_the_dialog_stays_open()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await MoveAsync(scenario, id, "test-parent-save", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(["Save"], StepNames(dialog)), Wait);

        circuit.Service.HistoryThrows = "secret: the history's query broke";
        Card(dialog).QuerySelector("[data-open-history]")!.Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains(StateHistoryDialog.ReadFailed, dialog.Host.Find("[data-state-history] [role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("secret", dialog.Host.Markup, StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);
        Assert.Single(dialog.Host.FindAll(".state-entity-dialog"));

        circuit.Service.HistoryThrows = null;
        dialog.Host.FindAll("[data-state-history] button").Single(button => button.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".state-history__steps > li")), Wait);
    }

    [Fact]
    public async Task A_new_entity_asks_nothing_of_its_history()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenCreateAsync();

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("No steps yet.", Card(dialog).TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(Card(dialog).QuerySelectorAll("[role=alert]"));
        Assert.Equal((0, 0), (dialog.Context().HistoryRequests, circuit.Probe.HistoryReads));
    }

    [Fact]
    public async Task A_reader_who_is_no_owner_sees_the_state_and_its_description_alone_and_nothing_reads_or_offers_the_history()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, owners: false);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await MoveAsync(scenario, id, "test-parent-save", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.False(dialog.Context().MayReadHistory);
        Assert.Empty(dialog.Host.FindAll(".state-entity-dialog-header__history"));

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.False(Card(dialog).HasAttribute("hidden")), Wait);
        Assert.Contains("Being prepared", Card(dialog).TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Last steps", Card(dialog).TextContent, StringComparison.Ordinal);
        Assert.Empty(Card(dialog).QuerySelectorAll("[data-open-history], [data-history-step], [role=alert]"));

        Assert.Equal((0, 0), (dialog.Context().HistoryRequests, circuit.Probe.HistoryReads));

        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken);
        await dialog.Host.InvokeAsync(() => circuit.Dialogs.Close());
        var again = await circuit.OpenLoadedAsync(id);
        Assert.True(again.Context().MayReadHistory);
        Assert.NotEmpty(again.Host.FindAll(".state-entity-dialog-header__history"));
        again.Host.Find(".state-pill__button").Click();
        again.Host.WaitForAssertion(() => Assert.Equal(["Save"], StepNames(again)), Wait);
    }

    [Fact]
    public async Task Each_state_of_a_step_is_a_chip_in_its_own_colour_and_icon_and_the_full_history_takes_the_records_state_colour()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await MoveAsync(scenario, id, "test-parent-activate", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(["Active"], StepNames(dialog)), Wait);

        var chips = Card(dialog).QuerySelectorAll(".history-step__state");
        Assert.Equal(["Draft", "Active"], chips.Select(chip => chip.GetAttribute("data-history-state")));
        foreach (var (chip, state) in chips.Zip(new Enum[] { TestParentState.Draft, TestParentState.Active }))
        {
            var colors = ColorContrast.OnFill(state.GetStateColor());
            Assert.Equal($"--history-state-fill: {colors.Fill}; --history-state-text: {colors.Foreground};", chip.GetAttribute("style"));
            Assert.Equal(state.GetIcon(), chip.QuerySelector(".history-step__state-icon")!.TextContent.Trim());
            Assert.Contains(EnumDisplayHelper.GetDisplayName(state), chip.TextContent, StringComparison.Ordinal);
        }

        Card(dialog).QuerySelector("[data-open-history]")!.Click();
        dialog.Host.WaitForAssertion(() => Assert.NotEmpty(dialog.Host.FindAll(".state-history__steps > li")), Wait);
        var frame = DialogStateColors.For(TestParentState.Active.GetStateColor())!;
        var style = dialog.Host.FindAll(".form-dialog-shell").Last().GetAttribute("style") ?? string.Empty;
        Assert.Contains($"--dialog-frame-color: {frame.HeaderFill};", style, StringComparison.Ordinal);
        Assert.Contains($"--dialog-header-text: {frame.HeaderText};", style, StringComparison.Ordinal);
        Assert.Equal(["Draft", "Active"], dialog.Host.FindAll("[data-state-history] .history-step__state").Select(chip => chip.GetAttribute("data-history-state")));
    }

    [Fact]
    public async Task A_history_read_that_fails_says_so_in_the_card_and_never_shows_the_exceptions_text()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        circuit.Service.HistoryThrows = "secret: the history's query broke";
        var dialog = await circuit.OpenLoadedAsync(id);

        dialog.Host.Find(".state-pill__button").Click();

        dialog.Host.WaitForAssertion(() => Assert.Contains("could not be read", Card(dialog).QuerySelector("[role=alert]")!.TextContent, StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("secret", dialog.Host.Markup, StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);
    }

    private static IElement Card(OpenDialog dialog) => dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelector(".state-pill__card")!;

    private static PointerEventArgs Mouse => new() { PointerType = "mouse" };

    private static PointerEventArgs Touch => new() { PointerType = "touch" };

    private static List<string> StepNames(OpenDialog dialog) =>
        [.. Card(dialog).QuerySelectorAll(".history-step__name").Select(name => name.TextContent)];
}
