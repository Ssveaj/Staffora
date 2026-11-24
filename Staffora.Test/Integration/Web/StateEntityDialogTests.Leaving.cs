using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Lists;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

public partial class StateEntityDialogTests
{
    [Fact]
    public async Task The_leave_rule_runs_only_the_states_save_step_never_another_self_transition_or_a_forward_step()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission, TestParent.FilePermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        dialog.Host.Find("[name=test-parent-name]").Change(string.Empty);
        dialog.WaitForEdit();
        dialog.Host.WaitForAssertion(() => Assert.Contains("test-parent-file", dialog.StepKeys()), Wait);
        var writes = circuit.Probe.ParentWrites;
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        Assert.Contains("Name is required", dialog.Host.Find(".form-dialog .dialog-notice__message").TextContent, StringComparison.Ordinal);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);

        dialog.Host.Find("[name=test-parent-name]").Change("Spring period job");
        dialog.Host.Find("[name=test-parent-notes]").Change(TestParent.FailInTheSavesCondition);
        dialog.Host.WaitForAssertion(() => Assert.Contains("test-parent-activate", dialog.StepKeys()), Wait);
        Assert.False(dialog.Step("test-parent-activate").HasAttribute("aria-disabled"));
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        Assert.Contains(StateEntityDialogContext.CannotBeChecked, dialog.Host.Find(".form-dialog .dialog-notice__message").TextContent, StringComparison.Ordinal);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);

        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal(writes, circuit.Probe.ParentWrites);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Spring period job", TestParentState.Draft, (string?)null), (stored!.Name, stored.State, stored.Notes));
        Assert.True(context.HasChanges);
        Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog));
    }

    [Fact]
    public async Task The_leave_rule_reuses_the_footers_answer_for_the_version_and_its_save_runs_busy()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();
        dialog.WaitUntilAsked(1);

        dialog.Host.Find("[name=test-parent-notes]").Change("Bring the projector");
        dialog.WaitForEdit();
        circuit.Probe.HoldOtherReads();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);
        dialog.Host.WaitForAssertion(() => Assert.True(dialog.Host.Find(".state-entity-dialog__body").HasAttribute("inert")), Wait);
        Assert.True(context.IsBusy);
        Assert.True(dialog.Host.Find(".state-entity-dialog-header__close").HasAttribute("disabled"));
        Assert.All(dialog.Host.FindAll("[role=tab]"), tab => Assert.Equal("true", tab.GetAttribute("aria-disabled")));

        circuit.Probe.ReleaseOtherReads();
        WaitForRows(dialog, 0);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);

        dialog.WaitUntilAsked(2);
        await Task.Delay(200, cancellationToken);
        Assert.Equal(2, context.OfferRequests);
        Assert.False(context.IsBusy);
    }

    [Fact]
    public async Task While_the_leave_rule_asks_the_tabs_are_disabled_and_nothing_else_settles_or_hops()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var parent = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 0);

        await dialog.Host.InvokeAsync(() =>
        {
            parent.Entity!.Name = string.Empty;
            parent.NotifyEdited();
        });
        dialog.WaitForEdit();
        Task<bool>? reload = null;
        await dialog.Host.InvokeAsync(() => { reload = parent.ReloadAsync(); });
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);

        dialog.Host.WaitForAssertion(() => Assert.All(dialog.Host.FindAll("[role=tab]"), tab => Assert.Equal("true", tab.GetAttribute("aria-disabled"))), Wait);
        SelectView(dialog, TestParentDialogContext.InfoView);
        Task<bool>? again = null;
        await dialog.Host.InvokeAsync(() => { again = parent.ReloadAsync(); });
        Assert.False(await again!.WaitAsync(Wait, cancellationToken));
        await Task.Delay(200, cancellationToken);
        Assert.Single(dialog.Host.FindAll(".form-dialog"));
        Assert.Equal(TestParentDialogContext.ChildrenView, SelectedView(dialog));

        dialog.ConfirmButton("Keep editing").Click();
        Assert.False(await reload!.WaitAsync(Wait, cancellationToken));
        dialog.Host.WaitForAssertion(() => Assert.All(dialog.Host.FindAll("[role=tab]"), tab => Assert.False(tab.HasAttribute("aria-disabled"))), Wait);
        Assert.True(parent.HasChanges);
        Assert.Equal(1, parent.Version);
    }

    [Fact]
    public async Task In_create_mode_the_leave_rule_asks_and_never_creates_the_record()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenCreateAsync();
        var context = dialog.Context();
        var name = $"Never created {Guid.NewGuid():N}";
        dialog.Host.Find("[name=test-parent-name]").Change(name);
        dialog.WaitForEdit();

        Task<bool>? reload = null;
        await dialog.Host.InvokeAsync(() => { reload = context.ReloadAsync(); });
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        Assert.Contains("aren't saved automatically when you leave", dialog.Host.Find(".form-dialog .dialog-notice__message").TextContent, StringComparison.Ordinal);
        dialog.ConfirmButton("Keep editing").Click();
        Assert.False(await reload!.WaitAsync(Wait, cancellationToken));

        await dialog.Host.InvokeAsync(() => { reload = context.ReloadAsync(); });
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        Assert.True(await reload!.WaitAsync(Wait, cancellationToken));
        dialog.Host.WaitForAssertion(() => Assert.Equal(string.Empty, dialog.Host.Find("[name=test-parent-name]").GetAttribute("value")), Wait);
        Assert.True(context.IsCreate);
        Assert.False(context.HasSaved);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.AnyAsync(row => row.Name == name, cancellationToken));
    }

    [Fact]
    public void A_session_of_several_records_refuses_a_new_one_and_a_session_of_one_takes_it()
    {
        var created = new TestParentDialogContext(null!, null);
        var saved = new TestParentDialogContext(null!, Guid.NewGuid());

        var refused = Assert.Throws<ArgumentException>(() => new StateEntityDialogSession([
            StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(created),
            StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(saved)]));
        Assert.StartsWith("A session of several records shows saved rows only, and a new TestParent has none", refused.Message, StringComparison.Ordinal);

        Assert.Same(created, new StateEntityDialogSession([StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(created)]).Current.Context);
    }

    [Fact]
    public async Task A_move_whose_save_the_server_refuses_asks_and_after_a_discard_the_session_moves_on()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (session, _) = await OpenSessionOfAsync(circuit, [first, second]);

        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestParents.Where(row => row.Id == first).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, TestParentState.Active), cancellationToken);
        }

        session.Host.Find("[name=test-parent-notes]").Change("Mine");
        session.WaitForEdit();
        session.Host.Find("[data-session-step=next]").Click();
        session.Host.WaitForAssertion(() => Assert.StartsWith("The changes could not be saved.", session.Host.Find(".form-dialog .dialog-notice__message").TextContent, StringComparison.Ordinal), Wait);
        session.ConfirmButton("Keep editing").Click();
        session.Host.WaitForAssertion(() => Assert.Contains("someone else", session.Alerts(), StringComparison.Ordinal), Wait);
        Assert.Equal("1 of 2", Position(session));

        session.Host.Find("[data-session-step=next]").Click();
        session.Host.WaitForAssertion(() => Assert.Single(session.Host.FindAll(".form-dialog")), Wait);
        session.ConfirmButton("Discard changes").Click();
        session.Host.WaitForAssertion(() => Assert.Equal("2 of 2", Position(session)), Wait);
        Assert.Equal("Autumn period job", session.Title());
        session.Host.Find("[data-session-step=previous]").Click();
        session.Host.WaitForAssertion(() => Assert.Equal("1 of 2", Position(session)), Wait);
        Assert.Equal(TestParentState.Active, session.Context().Entity!.State);
        Assert.Null(session.Context().Entity!.Notes);
        Assert.Empty(await HistoryOfAsync(first, cancellationToken));
    }

    [Fact]
    public async Task While_a_session_loads_its_next_record_the_one_on_screen_is_held_busy_and_inert_so_nothing_done_on_it_is_swapped_away()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);
        var shown = dialog.Context();
        var start = circuit.Navigation.Uri;

        circuit.Probe.HoldLoads();
        dialog.Host.Find("[data-session-step=next]").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);

        dialog.Host.WaitForAssertion(() => Assert.True(dialog.Host.Find(".state-entity-dialog__body").HasAttribute("inert")), Wait);
        Assert.True(shown.IsBusy);
        Assert.True(dialog.Host.Find("[role=tablist]").HasAttribute("inert"));
        Assert.True(dialog.Host.Find(".state-entity-dialog-footer__bar").HasAttribute("inert"));
        Assert.All(dialog.Host.FindAll("[role=tab]"), tab => Assert.Equal("true", tab.GetAttribute("aria-disabled")));
        Assert.Equal("true", dialog.Step("test-parent-save").GetAttribute("aria-disabled"));
        Assert.Equal("true", dialog.Host.Find(".state-entity-dialog-session").GetAttribute("aria-busy"));
        Assert.All(dialog.Host.FindAll("[data-session-step]"), step => Assert.Equal("true", step.GetAttribute("aria-disabled")));

        var writes = circuit.Probe.ParentWrites;
        await TryClickAsync(dialog, "button[data-transition='test-parent-save']");
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.Find("[data-session-step=next]").Click();
        await dialog.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        dialog.Host.WaitForAssertion(() => Assert.Equal(StateEntityDialogSession.NavigationHeldWhileMoving, dialog.Status()), Wait);
        Assert.Equal(start, circuit.Navigation.Uri);
        await Task.Delay(200, cancellationToken);
        Assert.Equal(writes, circuit.Probe.ParentWrites);
        Assert.Empty(dialog.Alerts());
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));
        Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog));
        Assert.Equal(1, session.Preloads);

        circuit.Probe.ReleaseLoads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);
        Assert.False(shown.IsBusy);
        Assert.False(shown.IsHeldByMove);
        Assert.Empty(await HistoryOfAsync(first, cancellationToken));
    }

    [Fact]
    public async Task A_record_the_session_keeps_with_edits_is_never_reloaded_unasked_and_closing_or_navigating_asks_about_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, _) = await OpenSessionOfAsync(circuit, [first, second]);
        var held = dialog.Context();
        var start = circuit.Navigation.Uri;

        circuit.Probe.HoldLoads();
        dialog.Host.Find("[data-session-step=next]").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);
        await dialog.Host.InvokeAsync(() =>
        {
            held.Entity!.Notes = "Typed while the next loaded";
            held.NotifyEdited();
        });
        circuit.Probe.ReleaseLoads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);
        Assert.True(held.HasChanges);

        await dialog.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal(start, circuit.Navigation.Uri);

        dialog.Host.Find("[name=test-parent-name]").Change("Autumn period job, edited");
        dialog.WaitForEdit();
        await dialog.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes in 2 records?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);

        var loads = circuit.Probe.Loads;
        dialog.Host.Find("[data-session-step=previous]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("1 of 2", Position(dialog)), Wait);
        Assert.Equal("Autumn period job, edited", (await LoadAsync(second, cancellationToken))!.Name);
        Assert.Equal("Typed while the next loaded", dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value"));
        Assert.True(held.HasChanges);
        Assert.Equal(loads + 1, circuit.Probe.Loads);

        _ = dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        Assert.True((await dialog.Result.WaitAsync(Wait, cancellationToken)).Changed);
        Assert.Null((await LoadAsync(first, cancellationToken))!.Notes);
    }

    [Fact]
    public async Task Closing_a_session_asks_about_a_record_it_keeps_with_edits_though_another_is_shown()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, _) = await OpenSessionOfAsync(circuit, [first, second]);
        var held = dialog.Context();

        circuit.Probe.HoldLoads();
        dialog.Host.Find("[data-session-step=next]").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);
        await dialog.Host.InvokeAsync(() =>
        {
            held.Entity!.Notes = "Typed while the next loaded";
            held.NotifyEdited();
        });
        circuit.Probe.ReleaseLoads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);

        _ = dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.False(dialog.Result.IsCompleted);
        Assert.Equal("2 of 2", Position(dialog));
        Assert.True(held.HasChanges);
    }

    [Fact]
    public async Task Closing_while_a_session_loads_its_next_record_stops_the_load_and_closes_on_the_record_shown()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);

        circuit.Probe.HoldLoads();
        dialog.Host.Find("[data-session-step=next]").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);
        await dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));

        Assert.Equal(new StateEntityDialogResult(false, false, first), await dialog.Result.WaitAsync(Wait, cancellationToken));
        await circuit.Probe.LoadCancelled.Task.WaitAsync(Wait, cancellationToken);

        await Task.Delay(200, cancellationToken);
        Assert.Equal(0, session.CurrentIndex);
        Assert.All(session.Targets, target => Assert.True(target.Context.Lifetime.IsCancellationRequested));
        Assert.Empty(circuit.UrlState.DialogStack);
    }

    [Fact]
    public async Task The_session_does_not_close_while_its_move_asks_about_the_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);

        dialog.Host.Find("[name=test-parent-name]").Change(string.Empty);
        dialog.WaitForEdit();
        dialog.Host.Find("[data-session-step=next]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);

        Task<bool>? mayClose = null;
        await dialog.Host.InvokeAsync(() => { mayClose = session.MayCloseAsync(circuit.Dialogs); });
        Assert.False(await mayClose!.WaitAsync(Wait, cancellationToken));
        Assert.Single(dialog.Host.FindAll(".form-dialog"));
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", dialog.Host.Find(".state-entity-dialog-session").GetAttribute("aria-busy")), Wait);
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task A_second_move_while_one_asks_and_a_move_while_a_step_runs_do_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        var third = await AddParentAsync(scenario.A, TestParentState.Draft, "Winter period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second, third]);

        dialog.Host.Find("[name=test-parent-name]").Change(string.Empty);
        dialog.WaitForEdit();
        dialog.Host.Find("[data-session-step=next]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.Host.Find("[data-session-step=next]").Click();
        await Task.Delay(200, cancellationToken);
        Assert.Single(dialog.Host.FindAll(".form-dialog"));
        Assert.True(session.IsSwapping);
        Assert.Equal("true", dialog.Host.Find(".state-entity-dialog-session").GetAttribute("aria-busy"));
        dialog.ConfirmButton("Discard changes").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("2 of 3", Position(dialog)), Wait);

        dialog.Host.WaitForAssertion(() => Assert.Contains("test-parent-activate", dialog.StepKeys()), Wait);
        circuit.Probe.HoldOtherReads();
        dialog.Step("test-parent-activate").Click();
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);
        var loads = circuit.Probe.Loads;
        dialog.Host.Find("[data-session-step=next]").Click();
        await Task.Delay(200, cancellationToken);
        Assert.Equal(loads, circuit.Probe.Loads);
        Assert.Equal(1, session.Preloads);
        circuit.Probe.ReleaseOtherReads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Status()), Wait);
        Assert.Equal("2 of 3", Position(dialog));
    }

    [Fact]
    public async Task A_swap_hands_the_opener_to_the_next_heading_and_only_the_close_releases_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var module = circuit.Page.JSInterop.SetupModule(StateEntityDialogHeader.ModulePath);
        var (dialog, _) = await OpenSessionOfAsync(circuit, [first, second]);
        var firstTitle = dialog.Host.FindComponent<StateEntityDialogHeader>().Instance.TitleId;

        dialog.Host.Find("[data-session-step=next]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal(firstTitle, Assert.Single(module.Invocations["handOffDialog"]).Arguments[0]), Wait);
        Assert.Empty(module.Invocations["releaseDialog"]);
        var secondTitle = dialog.Host.FindComponent<StateEntityDialogHeader>().Instance.TitleId;

        await dialog.CloseByEscAsync();
        dialog.Host.WaitForAssertion(() => Assert.Equal(secondTitle, Assert.Single(module.Invocations["releaseDialog"]).Arguments[0]), Wait);
        Assert.Single(module.Invocations["handOffDialog"]);
    }

    [Fact]
    public async Task Closing_a_session_ends_every_record_it_keeps()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);
        dialog.Host.Find("[data-session-step=next]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("2 of 2", Position(dialog)), Wait);

        await dialog.CloseByEscAsync();

        Assert.All(session.Targets, target => Assert.True(target.Context.Lifetime.IsCancellationRequested));
    }

    [Fact]
    public async Task Deleting_the_record_shown_in_a_session_removes_it_and_shows_its_neighbour_and_deleting_the_last_closes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);
        var deleted = dialog.Context();

        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForElement(".form-dialog");
        dialog.ConfirmButton("Deleted").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);
        Assert.Single(session.Targets);
        Assert.Empty(dialog.Host.FindAll(".state-entity-dialog-session"));
        Assert.Single(dialog.Host.FindAll(".rz-dialog-wrapper"));
        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, second)], circuit.UrlState.DialogStack);
        Assert.True(deleted.Lifetime.IsCancellationRequested);
        Assert.False(dialog.Result.IsCompleted);
        Assert.Equal(TestParentState.Deleted, (await LoadAsync(first, cancellationToken))?.State ?? TestParentState.Deleted);

        dialog.WaitUntilAsked(dialog.Context().OfferRequests);
        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForElement(".form-dialog");
        dialog.ConfirmButton("Deleted").Click();
        Assert.Equal(new StateEntityDialogResult(true, true, second), await dialog.Result.WaitAsync(Wait, cancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigating_away_while_a_changed_child_is_open_over_a_parent_with_edits_asks_once_and_leaves_nothing_behind(bool savable)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var morning = await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var parent = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);

        await dialog.Host.InvokeAsync(() =>
        {
            if (savable)
            {
                parent.Entity!.Notes = "Discarded by the user";
            }
            else
            {
                parent.Entity!.Name = string.Empty;
            }

            parent.NotifyEdited();
        });
        dialog.WaitForEdit();
        await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);

        await dialog.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        dialog.Host.WaitForAssertion(() => Assert.EndsWith("/elsewhere", circuit.Navigation.Uri, StringComparison.Ordinal), Wait);

        await Task.Delay(500, cancellationToken);
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));
        Assert.Empty(dialog.Host.FindAll(".state-entity-dialog"));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Spring period job", (string?)null), (stored!.Name, stored.Notes));
        Assert.Equal(0, parent.ReloadRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_navigation_raised_in_Blazor_Servers_order_ends_the_parent_before_a_changed_childs_close_resumes_its_list(bool savable)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var morning = await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var parent = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);

        await dialog.Host.InvokeAsync(() =>
        {
            if (savable)
            {
                parent.Entity!.Notes = "Discarded by the user";
            }
            else
            {
                parent.Entity!.Name = string.Empty;
            }

            parent.NotifyEdited();
        });
        dialog.WaitForEdit();
        await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);

        var leaveRule = new List<string>();
        var opened = 0;
        circuit.Dialogs.OnOpen += (_, _, _, _) => opened++;
        parent.LeaveStepStarted += _ => leaveRule.Add("save started");
        parent.Changed += () =>
        {
            if (parent.IsSettling)
            {
                leaveRule.Add("settling");
            }
        };
        var reads = circuit.Probe.OtherParentReads;
        var writes = circuit.Probe.ParentWrites;

        var raise = typeof(Microsoft.AspNetCore.Components.NavigationManager).GetMethod("NotifyLocationChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(bool)])!;
        await dialog.Host.InvokeAsync(() => raise.Invoke(circuit.Navigation, [false]));
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".state-entity-dialog")), Wait);
        await Task.Delay(500, cancellationToken);

        Assert.Empty(leaveRule);
        Assert.Equal(0, opened);
        Assert.Equal(reads, circuit.Probe.OtherParentReads);
        Assert.Equal(writes, circuit.Probe.ParentWrites);
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Spring period job", (string?)null), (stored!.Name, stored.Notes));
        Assert.Equal(0, parent.ReloadRequests);
    }

    [Fact]
    public async Task While_a_session_loads_its_next_record_the_header_is_inert_and_a_dialog_opened_above_gives_the_move_up()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);
        var shown = dialog.Context();

        circuit.Probe.HoldLoads();
        dialog.Host.Find("[data-session-step=next]").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);

        dialog.Host.WaitForAssertion(() => Assert.True(dialog.Host.Find(".state-entity-dialog-header").HasAttribute("inert")), Wait);
        Assert.NotNull(dialog.Host.Find(".state-pill__button").Closest("[inert]"));

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForElement("[data-open-history]");
        dialog.Host.Find("[data-open-history]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.False(shown.IsBusy), Wait);
        Assert.False(dialog.Host.Find(".state-entity-dialog-header").HasAttribute("inert"));

        circuit.Probe.ReleaseLoads();
        await Task.Delay(300, cancellationToken);
        Assert.Equal("Spring period job", dialog.Title());
        Assert.Equal("1 of 2", Position(dialog));
        Assert.Equal(0, session.CurrentIndex);
        Assert.Equal("History: Spring period job", dialog.Host.Find(".form-dialog-header__title").TextContent);
        Assert.False(session.IsSwapping);

        await dialog.Host.InvokeAsync(() => circuit.Dialogs.Close());
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.Host.Find("[data-session-step=next]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);
        Assert.Equal("2 of 2", Position(dialog));
    }

    [Fact]
    public async Task Esc_while_a_session_loads_its_next_record_with_an_edit_gives_the_move_up_before_it_asks_and_closes_on_the_record_shown()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var module = circuit.Page.JSInterop.SetupModule(StateEntityDialogHeader.ModulePath);
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);
        var held = dialog.Context();

        circuit.Probe.HoldLoads();
        dialog.Host.Find("[data-session-step=next]").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);
        await dialog.Host.InvokeAsync(() =>
        {
            held.Entity!.Notes = "Typed while the next loaded";
            held.NotifyEdited();
        });
        _ = dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.Host.WaitForAssertion(() => Assert.False(held.IsBusy), Wait);

        circuit.Probe.ReleaseLoads();
        await Task.Delay(300, cancellationToken);
        Assert.Equal("1 of 2", Position(dialog));
        Assert.Equal("Spring period job", dialog.Title());
        Assert.Single(module.Invocations["nameDialog"]);
        Assert.Empty(module.Invocations["handOffDialog"]);

        dialog.ConfirmButton("Discard changes").Click();
        Assert.Equal(new StateEntityDialogResult(false, false, first), await dialog.Result.WaitAsync(Wait, cancellationToken));
        Assert.Equal(0, session.CurrentIndex);
        Assert.Null((await LoadAsync(first, cancellationToken))!.Notes);
    }

    [Fact]
    public async Task A_deleted_records_neighbour_is_swapped_in_only_once_the_dialog_above_has_closed()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var (dialog, session) = await OpenSessionOfAsync(circuit, [first, second]);

        circuit.Probe.HoldLoads();
        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForElement(".form-dialog");
        dialog.ConfirmButton("Deleted").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        _ = dialog.Host.InvokeAsync(() => circuit.Dialogs.ShowMessageAsync("Another dialog on top.", "On top"));
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);

        circuit.Probe.ReleaseLoads();
        await Task.Delay(300, cancellationToken);
        Assert.Equal("Spring period job", dialog.Title());
        Assert.Equal(2, session.Targets.Count);

        dialog.ConfirmButton("OK").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);
        Assert.Single(session.Targets);
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task A_header_that_goes_before_its_module_has_loaded_takes_the_hand_off_with_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        circuit.Page.JSInterop.AddInvocationHandler(new UnansweredImport(StateEntityDialogHeader.ModulePath));
        var (dialog, _) = await OpenSessionOfAsync(circuit, [first, second]);
        var swapped = dialog.Context();

        dialog.Host.Find("[data-session-step=next]").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", dialog.Title()), Wait);
        await Task.Delay(100, cancellationToken);
        Assert.False(swapped.TakeHandOff());
    }

    [Fact]
    public async Task Closing_a_child_that_changed_while_the_parent_has_edits_typed_beside_its_list_saves_them_before_the_parent_reloads()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var morning = await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var parent = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);

        dialog.Host.Find("[name=test-parent-children-notes]").Change("Two groups this period");
        dialog.WaitForEdit();
        await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);
        await CloseTopByEscAsync(dialog, layouts: 1);

        await WaitForNotesAsync(id, "Two groups this period", cancellationToken);
        Assert.Equal("test-parent-save", Assert.Single(await HistoryOfAsync(id, cancellationToken)).TransitionKey);
        dialog.Host.WaitForAssertion(() => Assert.False(parent.HasChanges), Wait);
        Assert.Equal("Two groups this period", dialog.Host.Find("[name=test-parent-children-notes]").GetAttribute("value"));
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active", dialog.Host.Find("[role=tabpanel][data-view=children] .list-state").TextContent), Wait);
    }

    [Fact]
    public async Task Closing_a_child_that_changed_while_the_parent_has_edits_typed_beside_its_list_that_no_save_takes_asks_before_the_parent_reloads()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var morning = await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var parent = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);

        dialog.Host.Find("[name=test-parent-children-notes]").Change(TestParent.FailInTheSavesCondition);
        dialog.WaitForEdit();
        var version = parent.Version;
        await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);
        await CloseTopByEscAsync(dialog, layouts: 1);

        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal(version, parent.Version);
        Assert.Equal(TestParent.FailInTheSavesCondition, dialog.Host.Find("[name=test-parent-children-notes]").GetAttribute("value"));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Closing_a_child_that_changed_over_a_parent_without_edits_reloads_the_parent()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var morning = await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var parent = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);
        var version = parent.Version;

        await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);
        await CloseTopByEscAsync(dialog, layouts: 1);

        dialog.Host.WaitForAssertion(() => Assert.Equal(1, parent.ReloadRequests), Wait);
        dialog.Host.WaitForAssertion(() => Assert.True(parent.Version > version), Wait);
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_visited_list_view_offered_again_reads_its_rows_only_once_it_is_selected_again()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var parent = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);
        SelectView(dialog, TestParentDialogContext.InfoView);
        dialog.Host.WaitForAssertion(() => Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog)), Wait);

        await SetStateAsync(id, TestParentState.Canceled, cancellationToken);
        Assert.True(await dialog.Host.InvokeAsync(parent.ReloadAsync));
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll("[role=tablist]")), Wait);

        await SetStateAsync(id, TestParentState.Draft, cancellationToken);
        Assert.True(await dialog.Host.InvokeAsync(parent.ReloadAsync));
        dialog.Host.WaitForAssertion(() => Assert.NotEmpty(dialog.Host.FindAll("[role=tablist]")), Wait);
        await Task.Delay(200, cancellationToken);
        Assert.Equal(1, circuit.Service.ChildQueries);
        Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog));
        Assert.Empty(dialog.Host.Find("[role=tabpanel][data-view=children]").Children);
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);
        Assert.Equal(2, circuit.Service.ChildQueries);
    }

    [Fact]
    public async Task A_list_whose_reload_is_cancelled_by_leaving_its_view_is_no_longer_busy()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);

        circuit.Service.BeforeChildQuery = token => Task.Delay(Timeout.Infinite, token);
        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(2, circuit.Service.ChildQueries), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Host.Find("[role=tabpanel][data-view=children] .list-view__frame").GetAttribute("aria-busy")), Wait);

        SelectView(dialog, TestParentDialogContext.InfoView);
        dialog.Host.WaitForAssertion(() => Assert.True(circuit.Service.ChildQueryTokens[1].IsCancellationRequested), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", dialog.Host.Find("[role=tabpanel][data-view=children] .list-view__frame").GetAttribute("aria-busy")), Wait);
        Assert.Single(dialog.Host.FindAll("[role=tabpanel][data-view=children] tr[data-row]"));
    }

    [Fact]
    public async Task A_circuit_that_cannot_check_a_views_permission_offers_no_such_view()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime(), withPermissionService: false);

        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Empty(dialog.Host.FindAll("[role=tablist]"));
        Assert.NotNull(dialog.Host.Find("[name=test-parent-name]"));
        Assert.Equal(0, circuit.Service.ChildQueries);
    }

    [Fact]
    public async Task The_children_views_load_checks_its_permission_and_the_tenant_on_the_server()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);

        await using (var host = ParentServiceHost(scenario.A.Runtime()))
        {
            var refused = await host.Get<TestParentDialogService>().QueryTestParentChildrenAsync(id, new ListQuery(), cancellationToken);
            Assert.False(refused.IsSuccess);
            Assert.Equal(TestParentDialogService.NoChildrenAccess, refused.Error);
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.ChildrenPermission);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, TestParentDialogService.ChildrenPermission);
        await using (var host = ParentServiceHost(scenario.A.Runtime()))
        {
            var own = await host.Get<TestParentDialogService>().QueryTestParentChildrenAsync(id, new ListQuery(), cancellationToken);
            Assert.True(own.IsSuccess, own.Error);
            Assert.Equal(1, own.Value!.TotalCount);
        }

        await using (var host = ParentServiceHost(scenario.B.Runtime()))
        {
            var foreign = await host.Get<TestParentDialogService>().QueryTestParentChildrenAsync(id, new ListQuery(), cancellationToken);
            Assert.True(foreign.IsSuccess, foreign.Error);
            Assert.Equal(0, foreign.Value!.TotalCount);
        }
    }

    private ServiceHost ParentServiceHost(Staffora.Core.Execution.IRuntimeExecutionContext runtime) =>
        ServiceHost.Create(this.database, runtime, services => services.AddScoped<TestParentDialogService>());

    private static async Task<(OpenDialog Dialog, StateEntityDialogSession Session)> OpenSessionOfAsync(DialogCircuit circuit, IReadOnlyList<Guid> ids)
    {
        var session = new StateEntityDialogSession([.. ids.Select(id => StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(TestParentDialog.ContextFor(circuit.Service, id)))]);
        Task<StateEntityDialogResult>? result = null;
        await circuit.Dialog.InvokeAsync(() => { result = circuit.Dialogs.OpenStateEntityDialogSessionAsync(session); });
        circuit.Dialog.WaitForAssertion(() => Assert.Equal("loaded", circuit.Dialog.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
        circuit.Dialog.WaitForAssertion(() => Assert.Equal("false", circuit.Dialog.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        return (new OpenDialog(circuit.Dialog, result!), session);
    }

    private sealed class UnansweredImport : JSRuntimeInvocationHandlerBase<Microsoft.JSInterop.IJSObjectReference>
    {
        public UnansweredImport(string path) : base(invocation => invocation.Identifier == "import" && Equals(invocation.Arguments.FirstOrDefault(), path), isCatchAllHandler: false)
        {
        }
    }

    private static string Position(OpenDialog dialog) => dialog.Host.Find(".state-entity-dialog-session__position").TextContent;

    private static async Task TryClickAsync(OpenDialog dialog, string selector)
    {
        try
        {
            await dialog.Host.Find(selector).ClickAsync(new());
        }
        catch (Bunit.MissingEventHandlerException)
        {
        }
    }

    private async Task SetStateAsync(Guid id, TestParentState state, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        await system.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, state), cancellationToken);
    }
}
