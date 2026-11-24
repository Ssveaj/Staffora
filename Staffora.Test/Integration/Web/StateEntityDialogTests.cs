using System.Data.Common;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public partial class StateEntityDialogTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public StateEntityDialogTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task The_dialog_shows_the_loader_and_the_default_title_while_its_one_load_runs()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        circuit.Probe.HoldLoads();

        var dialog = await circuit.OpenAsync(id);
        dialog.Host.WaitForElement(".state-entity-dialog__body > [data-loading]");

        Assert.Equal("status", dialog.Host.Find(".state-entity-dialog__body > [data-loading]").GetAttribute("role"));
        Assert.Equal("Parent", dialog.Title());
        Assert.Empty(dialog.Host.FindAll("[name=test-parent-name]"));
        Assert.Empty(dialog.Host.FindAll(".state-entity-dialog-footer"));
        Assert.Equal("loading", dialog.Host.Find(".state-entity-dialog").GetAttribute("data-load-state"));

        circuit.Probe.ReleaseLoads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Parent", dialog.Host.Find("[name=test-parent-name]").GetAttribute("value")), Wait);
        Assert.Empty(dialog.Host.FindAll(".state-entity-dialog__body > [data-loading]"));
        Assert.Equal(1, circuit.Probe.Loads);
    }

    [Fact]
    public async Task The_header_is_filled_with_the_states_colour_darkened_until_its_white_text_reaches_contrast()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var light = await AddParentAsync(scenario.A, TestParentState.Draft, "Light parent", cancellationToken);
        var dark = await AddParentAsync(scenario.A, TestParentState.Canceled, "Dark parent", cancellationToken);

        foreach (var (id, state) in new[] { (light, "#2196F3"), (dark, "#757575") })
        {
            await using var circuit = Circuit(scenario.A.Runtime());
            var dialog = await circuit.OpenAsync(id);
            dialog.Host.WaitForAssertion(() => Assert.Equal("loaded", dialog.Host.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);

            var style = dialog.Host.Find(".state-entity-dialog").GetAttribute("style") ?? string.Empty;
            var fill = ColorContrast.ForWhite(state).Fill;
            Assert.Contains($"--dialog-state-color: {state};", style, StringComparison.Ordinal);
            Assert.Contains($"--dialog-header-fill: {fill};", style, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"--dialog-header-text: {ColorContrast.White};", style, StringComparison.Ordinal);
            Assert.True(ColorContrast.Ratio(fill, ColorContrast.White) >= ColorContrast.TextMinimum, $"white on {fill}");
            var pill = DialogStateColors.For(state)!;
            Assert.Contains($"--dialog-pill-fill: {pill.PillFill}; --dialog-pill-text: {pill.PillText};", style, StringComparison.Ordinal);
            Assert.True(ColorContrast.Ratio(pill.PillFill, pill.PillText) >= ColorContrast.TextMinimum, $"{pill.PillText} on {pill.PillFill}");

            var header = dialog.Host.Find(".state-entity-dialog-header");
            Assert.Equal("History", header.QuerySelector(".state-entity-dialog-header__history")!.GetAttribute("aria-label"));
            Assert.Equal("Close", header.QuerySelector(".state-entity-dialog-header__close")!.GetAttribute("aria-label"));
            Assert.NotNull(header.QuerySelector(".state-pill__button"));
        }
    }

    [Fact]
    public async Task A_missing_and_another_tenants_id_both_show_could_not_open_with_the_same_reason()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var theirs = await AddParentAsync(scenario.B, TestParentState.Draft, "Their parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var reasons = new List<string>();
        foreach (var id in new[] { Guid.NewGuid(), theirs })
        {
            var dialog = await circuit.OpenAsync(id);
            dialog.Host.WaitForElement(".state-entity-dialog__could-not-open");
            var alert = dialog.Host.Find(".state-entity-dialog__could-not-open [role=alert]");
            reasons.Add(alert.TextContent);

            Assert.Empty(dialog.Host.FindAll("[name=test-parent-name]"));
            Assert.Empty(dialog.Host.FindAll(".state-entity-dialog-footer"));
            Assert.Equal("Parent", dialog.Title());
            Assert.DoesNotContain("Their parent", dialog.Host.Markup, StringComparison.Ordinal);

            await dialog.CloseByEscAsync();
            Assert.False((await dialog.Result).Changed);
        }

        Assert.All(reasons, reason => Assert.Contains(TestParentDialogService.NotFound, reason, StringComparison.Ordinal));
        Assert.Equal(reasons[0], reasons[1]);
    }

    [Fact]
    public async Task A_user_without_access_sees_could_not_open_with_the_services_reason()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenAsync(id);

        dialog.Host.WaitForAssertion(() => Assert.Contains(TestParentDialogService.NoAccess, dialog.Host.Find(".state-entity-dialog__could-not-open [role=alert]").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task A_load_that_throws_shows_a_plain_reason_and_never_the_exceptions_text()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        circuit.Service.LoadThrows = "secret: the load's query broke";

        var dialog = await circuit.OpenAsync(id);

        dialog.Host.WaitForAssertion(() => Assert.Contains(StateEntityDialogContext.LoadFailed, dialog.Host.Find(".state-entity-dialog__could-not-open [role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("secret", dialog.Host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_dialog_while_it_loads_cancels_the_loads_query()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        circuit.Probe.HoldLoads();

        var dialog = await circuit.OpenAsync(id);
        dialog.Host.WaitForElement(".state-entity-dialog__body > [data-loading]");
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);

        await dialog.CloseByEscAsync();

        await circuit.Probe.LoadCancelled.Task.WaitAsync(Wait, cancellationToken);
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".state-entity-dialog")), Wait);
        Assert.Equal(new StateEntityDialogResult(false, false, id), await dialog.Result);
    }

    [Fact]
    public async Task Closing_with_an_edit_by_the_close_button_or_by_Esc_asks_to_confirm_and_keeps_the_edit_until_confirmed()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        DialogOptions? options = null;
        circuit.Dialogs.OnOpen += (_, _, _, opened) => options ??= opened;
        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.NotNull(options);
        Assert.True(options.CloseDialogOnEsc);
        Assert.False(options.CloseDialogOnOverlayClick);
        Assert.False(options.AutoFocusFirstElement);
        Assert.NotNull(options.CanClose);

        dialog.Host.Find("[name=test-parent-name]").Change("Renamed");

        dialog.Host.Find(".state-entity-dialog-header__close").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal("Renamed", dialog.Host.Find("[name=test-parent-name]").GetAttribute("value"));
        Assert.False(dialog.Result.IsCompleted);

        _ = dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Discard changes").Click();

        Assert.False((await dialog.Result.WaitAsync(Wait, cancellationToken)).Changed);
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".state-entity-dialog")), Wait);
        Assert.Equal("Parent", (await LoadAsync(id, cancellationToken))!.Name);
    }

    [Fact]
    public async Task Without_an_edit_Esc_closes_the_dialog_at_once_and_its_lifetime_is_disposed()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        await dialog.CloseByEscAsync();

        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".state-entity-dialog")), Wait);

        Assert.True(context.Lifetime.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => context.Lifetime.WaitHandle);
    }

    [Fact]
    public async Task A_step_into_the_delete_state_closes_the_dialog_and_answers_deleted()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken, withChild: true);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, id)], circuit.UrlState.DialogStack);

        var delete = dialog.Step("test-parent-delete");
        Assert.NotNull(delete.Closest(".state-entity-dialog-footer__leading"));
        delete.Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("Delete this parent and everything below it?", dialog.Host.Find(".form-dialog .dialog-notice__message").TextContent, StringComparison.Ordinal), Wait);
        dialog.ConfirmButton("Deleted").Click();

        Assert.Equal(new StateEntityDialogResult(true, true, id), await dialog.Result.WaitAsync(Wait, cancellationToken));
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".state-entity-dialog")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Empty(circuit.UrlState.DialogStack), Wait);
        Assert.Null(await LoadAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_dialog_deleted_while_another_dialog_is_above_it_closes_itself_once_that_one_has_closed()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        Task<bool>? above = null;
        await dialog.Host.InvokeAsync(() => { above = circuit.Dialogs.ConfirmAsync("Something else?", "Another dialog"); });
        dialog.Host.WaitForElement(".form-dialog");
        var attempt = await dialog.Host.InvokeAsync(() => context.RunTransitionAsync(context.Workflow.GetTransition("test-parent-delete"), null));
        Assert.True(attempt.Ran, attempt.Error);

        Assert.Single(dialog.Host.FindAll(".form-dialog"));
        Assert.False(above!.IsCompleted);
        Assert.False(dialog.Result.IsCompleted);

        dialog.ConfirmButton("Cancel").Click();
        Assert.False(await above.WaitAsync(Wait, cancellationToken));
        Assert.Equal(new StateEntityDialogResult(true, true, id), await dialog.Result.WaitAsync(Wait, cancellationToken));
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".state-entity-dialog")), Wait);
    }

    [Fact]
    public async Task A_declined_delete_confirmation_runs_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForElement(".form-dialog");
        dialog.ConfirmButton("Cancel").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);

        Assert.NotNull(await LoadAsync(id, cancellationToken));
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task A_delete_takes_the_row_as_it_is_stored_without_the_bodys_check_or_a_save_of_the_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var checks = new List<string>();
        dialog.Context().OnBeforeTransition = transition =>
        {
            checks.Add(transition.Key);
            return Task.FromResult(true);
        };

        dialog.Host.Find("[name=test-parent-name]").Change(new string('x', 201));
        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForElement(".form-dialog");
        dialog.ConfirmButton("Deleted").Click();

        Assert.Equal(new StateEntityDialogResult(true, true, id), await dialog.Result.WaitAsync(Wait, cancellationToken));
        Assert.Empty(checks);
        Assert.Null(await LoadAsync(id, cancellationToken));
    }

    [Fact]
    public async Task Navigating_away_with_an_edit_asks_and_keeps_the_dialog_and_the_edit_when_the_user_keeps_editing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var start = circuit.Navigation.Uri;
        dialog.Host.Find("[name=test-parent-name]").Change("Renamed");

        await dialog.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal(start, circuit.Navigation.Uri);
        Assert.Equal("Renamed", dialog.Host.Find("[name=test-parent-name]").GetAttribute("value"));
        Assert.False(dialog.Result.IsCompleted);

        await dialog.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        Assert.False((await dialog.Result.WaitAsync(Wait, cancellationToken)).Changed);
        dialog.Host.WaitForAssertion(() => Assert.EndsWith("/elsewhere", circuit.Navigation.Uri, StringComparison.Ordinal), Wait);
        Assert.Equal("Parent", (await LoadAsync(id, cancellationToken))!.Name);
    }

    [Fact]
    public async Task A_navigation_while_a_step_runs_is_held_back_and_the_step_completes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var start = circuit.Navigation.Uri;

        circuit.Probe.HoldOtherReads();
        dialog.Step("test-parent-activate").Click();
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);

        await dialog.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        dialog.Host.WaitForAssertion(() => Assert.Equal(StateEntityDialogContext.NavigationHeldWhileBusy, dialog.Host.Find(".state-entity-dialog-footer__status[role=status]").TextContent), Wait);
        Assert.Equal(start, circuit.Navigation.Uri);
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));

        circuit.Probe.ReleaseOtherReads();
        await WaitForStateAsync(id, TestParentState.Active, cancellationToken);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Status()), Wait);
        Assert.Equal(start, circuit.Navigation.Uri);
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task Two_stacked_dialogs_with_edits_ask_once_for_one_navigation_and_the_answer_holds_for_both()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "First", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Second", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var below = await circuit.OpenLoadedAsync(first);
        below.Host.FindAll("[name=test-parent-name]").Last().Change("First edited");
        var above = await circuit.OpenLoadedAsync(second);
        above.Host.FindAll("[name=test-parent-name]").Last().Change("Second edited");
        var start = circuit.Navigation.Uri;

        await above.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        above.Host.WaitForAssertion(() => Assert.Single(above.Host.FindAll(".form-dialog")), Wait);
        await Task.Delay(300, cancellationToken);
        Assert.Equal("Discard changes in 2 records?", Assert.Single(above.Host.FindAll(".form-dialog-header__title")).TextContent);

        above.ConfirmButton("Keep editing").Click();
        above.Host.WaitForAssertion(() => Assert.Empty(above.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal(start, circuit.Navigation.Uri);
        Assert.Equal(["First edited", "Second edited"], above.Host.FindAll("[name=test-parent-name]").Select(input => input.GetAttribute("value")));

        await above.Host.InvokeAsync(() => circuit.Navigation.NavigateTo("/elsewhere"));
        above.Host.WaitForAssertion(() => Assert.Single(above.Host.FindAll(".form-dialog")), Wait);
        above.ConfirmButton("Discard changes").Click();
        Assert.False((await above.Result.WaitAsync(Wait, cancellationToken)).Changed);
        Assert.False((await below.Result.WaitAsync(Wait, cancellationToken)).Changed);
        above.Host.WaitForAssertion(() => Assert.EndsWith("/elsewhere", circuit.Navigation.Uri, StringComparison.Ordinal), Wait);
        Assert.Equal(("First", "Second"), ((await LoadAsync(first, cancellationToken))!.Name, (await LoadAsync(second, cancellationToken))!.Name));
    }

    [Fact]
    public async Task The_browsers_prompt_on_leaving_the_app_follows_the_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        Assert.False(dialog.Host.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation);

        dialog.Host.Find("[name=test-parent-name]").Change("Renamed");
        dialog.Host.WaitForAssertion(() => Assert.True(dialog.Host.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation), Wait);
        dialog.Host.Find("[name=test-parent-name]").Change("Parent");
        dialog.Host.WaitForAssertion(() => Assert.False(dialog.Host.FindComponent<NavigationLock>().Instance.ConfirmExternalNavigation), Wait);
    }

    [Fact]
    public async Task A_step_its_conditions_block_or_the_access_gate_refuses_is_not_drawn()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, TestParentDialogService.ViewPermission, RolePermission.SettingsEdit);
        var nameless = await AddParentAsync(scenario.A, TestParentState.Draft, string.Empty, cancellationToken);
        var active = await AddParentAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken);

        await using (var circuit = Circuit(scenario.A.Runtime()))
        {
            var draft = await circuit.OpenLoadedAsync(nameless);
            Assert.Equal(["test-parent-delete", "test-parent-activate"], draft.StepKeys());
            Assert.Empty(draft.Host.FindAll("[data-transition=test-parent-save]"));
            await draft.CloseByEscAsync();

            var refused = await circuit.OpenLoadedAsync(active);
            Assert.Empty(refused.StepKeys());
        }

        await using var holders = Circuit(scenario.A.RuntimeOf(holder));
        var granted = await holders.OpenLoadedAsync(active);
        Assert.Equal(["test-parent-cancel"], granted.StepKeys());
    }

    [Fact]
    public async Task A_system_driven_step_is_never_offered()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Canceled, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Empty(dialog.StepKeys());
    }

    [Fact]
    public async Task A_ShowWhenBlocked_step_is_greyed_with_its_conditions_description_and_a_click_runs_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, string.Empty, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        var activate = dialog.Step("test-parent-activate");
        Assert.Equal("true", activate.GetAttribute("aria-disabled"));
        Assert.False(activate.HasAttribute("disabled"));
        Assert.Contains("transition-button--blocked", activate.Closest(".transition-button")!.ClassName, StringComparison.Ordinal);
        var card = dialog.Host.Find($"#{activate.GetAttribute("aria-describedby")}");
        Assert.Contains("Name is required", card.TextContent, StringComparison.Ordinal);

        var readsBefore = circuit.Probe.OtherParentReads;
        activate.Click();
        Assert.Equal(TestParentState.Draft, (await LoadAsync(id, cancellationToken))!.State);
        Assert.Equal(readsBefore, circuit.Probe.OtherParentReads);
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
    }

    [Fact]
    public async Task The_steps_are_asked_of_the_server_once_per_entity_version_and_not_per_render()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();
        Assert.Equal((1, 1, 1), (context.Version, context.OfferRequests, circuit.Probe.Loads));
        var reads = circuit.Probe.OtherParentReads;

        for (var round = 0; round < 3; round++)
        {
            dialog.Host.Render();
            await dialog.Host.InvokeAsync(context.NotifyChanged);
            dialog.Host.FindComponent<StateEntityDialogFooter>().Render();
        }

        dialog.Host.Find("[name=test-parent-notes]").Change("A note");
        dialog.WaitForEdit();
        Assert.Equal(["test-parent-delete", "test-parent-save", "test-parent-activate"], dialog.StepKeys());
        Assert.Equal((1, 1), (context.Version, context.OfferRequests));
        Assert.Equal(reads, circuit.Probe.OtherParentReads);

        dialog.Step("test-parent-save").Click();
        dialog.WaitUntilAsked(2);
        Assert.Equal(2, circuit.Probe.Loads);
        var version = context.Version;
        Assert.True(version > 1);
        Assert.Equal(2, context.OfferRequests);
        reads = circuit.Probe.OtherParentReads;
        for (var round = 0; round < 3; round++)
        {
            dialog.Host.Render();
            await dialog.Host.InvokeAsync(context.NotifyChanged);
        }

        Assert.Equal(3, dialog.StepKeys().Count);
        Assert.Equal((version, 2), (context.Version, context.OfferRequests));
        Assert.Equal(reads, circuit.Probe.OtherParentReads);
        Assert.Equal("A note", (await LoadAsync(id, cancellationToken))!.Notes);
    }

    [Fact]
    public async Task An_answer_for_a_version_the_dialog_no_longer_shows_is_discarded_and_the_old_steps_go_at_once()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();
        Assert.Equal(3, dialog.StepKeys().Count);

        circuit.Probe.HoldOtherReads();
        await dialog.Host.InvokeAsync(context.ReloadEntityAsync);
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        Assert.Empty(dialog.StepKeys());

        await dialog.Host.InvokeAsync(context.ReloadEntityAsync);
        await circuit.Probe.WaitForHeldOtherReadsAsync(2, cancellationToken);

        circuit.Probe.ReleaseOtherRead(0);
        await Task.Delay(300, cancellationToken);
        Assert.Equal("true", dialog.Host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy"));
        Assert.Empty(dialog.StepKeys());

        circuit.Probe.ReleaseOtherReads();
        dialog.Host.WaitForAssertion(() => Assert.Equal(3, dialog.StepKeys().Count), Wait);
        Assert.Equal("false", dialog.Host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy"));
        Assert.Equal(3, context.OfferRequests);
    }

    [Fact]
    public async Task A_refused_step_shows_its_conditions_description_a_throwing_condition_blocks_only_its_own_step_and_no_exception_text_shows()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        Assert.Equal("false", dialog.Step("test-parent-activate").GetAttribute("aria-disabled") ?? "false");

        await ClearNameAsync(id, cancellationToken);
        dialog.Step("test-parent-activate").Click();

        dialog.Host.WaitForAssertion(() => Assert.Contains("Name is required", dialog.Alerts(), StringComparison.Ordinal), Wait);
        Assert.Equal(TestParentState.Draft, (await LoadAsync(id, cancellationToken))!.State);

        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Step("test-parent-activate").GetAttribute("aria-disabled")), Wait);

        await UpdateParentAsync(id, parent => (parent.Name, parent.Notes) = ("Parent", TestParent.FailInACondition), cancellationToken);
        await dialog.Host.InvokeAsync(() => dialog.Context().ReloadEntityAsync());
        dialog.Host.WaitForAssertion(() => Assert.Equal(["test-parent-delete", "test-parent-save", "test-parent-activate"], dialog.StepKeys()), Wait);
        var blocked = dialog.Step("test-parent-activate");
        Assert.Equal("true", blocked.GetAttribute("aria-disabled"));
        Assert.Contains(WorkflowService.StepCannotBeChecked, dialog.Host.Find($"#{blocked.GetAttribute("aria-describedby")}").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("The activation's condition failed.", dialog.Host.Markup, StringComparison.Ordinal);

        circuit.Probe.FailOtherReads = true;
        await dialog.Host.InvokeAsync(() => dialog.Context().ReloadEntityAsync());
        dialog.Host.WaitForAssertion(() => Assert.Contains("could not be read", dialog.Alerts(), StringComparison.Ordinal), Wait);
        Assert.Empty(dialog.StepKeys());

        circuit.Probe.FailOtherReads = false;
        await UpdateParentAsync(id, parent => parent.Notes = null, cancellationToken);
        await dialog.Host.InvokeAsync(() => dialog.Context().ReloadEntityAsync());
        dialog.Host.WaitForAssertion(() => Assert.Equal(3, dialog.StepKeys().Count), Wait);
        Assert.DoesNotContain("could not be read", dialog.Alerts(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_a_transition_holds_is_judged_by_the_server_even_once_the_form_is_edited()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsInTransition, true), cancellationToken);
        }

        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        Assert.Equal(["test-parent-activate"], dialog.StepKeys());

        dialog.Host.Find("[name=test-parent-notes]").Change("A note");
        dialog.WaitForEdit();
        Assert.Equal(["test-parent-activate"], dialog.StepKeys());
        Assert.Equal("true", dialog.Step("test-parent-activate").GetAttribute("aria-disabled"));
    }

    [Fact]
    public async Task A_step_that_throws_unexpectedly_shows_a_plain_message_and_never_the_exceptions_text()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        circuit.Service.SaveThrows = "secret: the service broke";

        dialog.Host.Find("[name=test-parent-notes]").Change("A note");
        dialog.Step("test-parent-save").Click();

        dialog.Host.WaitForAssertion(() => Assert.Contains(TransitionButton.Unexpected, dialog.Alerts(), StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("secret", dialog.Host.Markup, StringComparison.Ordinal);
        Assert.Null((await LoadAsync(id, cancellationToken))!.Notes);
    }

    [Fact]
    public async Task A_refused_steps_message_stays_until_the_next_step_starts()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        circuit.Service.SaveThrows = "the service broke";

        dialog.Host.Find("[name=test-parent-notes]").Change("A note");
        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains(TransitionButton.Unexpected, dialog.Alerts(), StringComparison.Ordinal), Wait);

        dialog.Host.Find("[name=test-parent-notes]").Change("Another note");
        dialog.WaitForEdit();
        Assert.Contains(TransitionButton.Unexpected, dialog.Alerts(), StringComparison.Ordinal);
        circuit.Service.SaveThrows = null;
        circuit.Probe.HoldOtherReads();
        dialog.Step("test-parent-save").Click();
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);
        dialog.Host.WaitForAssertion(() => Assert.DoesNotContain(TransitionButton.Unexpected, dialog.Alerts(), StringComparison.Ordinal), Wait);

        circuit.Probe.ReleaseOtherReads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        Assert.Equal("Another note", (await LoadAsync(id, cancellationToken))!.Notes);
    }

    [Fact]
    public async Task While_a_step_runs_one_click_at_a_time_the_dialog_stays_open_and_the_body_is_inert()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        circuit.Probe.HoldOtherReads();
        dialog.Step("test-parent-save").Click();
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);

        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Step("test-parent-save").GetAttribute("aria-busy")), Wait);
        Assert.All(dialog.Host.FindAll("button[data-transition]"), button => Assert.Equal(("true", false), (button.GetAttribute("aria-disabled"), button.HasAttribute("disabled"))));

        Assert.True(dialog.Host.Find(".state-entity-dialog__body").HasAttribute("inert"));
        Assert.True(dialog.Host.Find(".state-entity-dialog-header__close").HasAttribute("disabled"));

        dialog.Step("test-parent-activate").Click();
        Assert.False(await circuit.Probe.WaitForHeldOtherReadsAsync(2, cancellationToken, TimeSpan.FromMilliseconds(500)));

        await dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        Assert.False(dialog.Result.IsCompleted);
        Assert.Single(dialog.Host.FindAll(".state-entity-dialog"));

        circuit.Probe.ReleaseOtherReads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        Assert.False(dialog.Host.Find(".state-entity-dialog__body").HasAttribute("inert"));
        Assert.Equal(["test-parent-save"], (await HistoryOfAsync(id, cancellationToken)).Select(entry => entry.TransitionKey));
        Assert.Equal(TestParentState.Draft, (await LoadAsync(id, cancellationToken))!.State);
    }

    [Fact]
    public async Task Closing_the_dialog_while_a_step_runs_cancels_the_steps_server_call()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        circuit.Probe.HoldOtherReads();
        dialog.Step("test-parent-activate").Click();
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);

        await dialog.Host.InvokeAsync(() => dialog.Context().End());

        await circuit.Probe.OtherReadCancelled.Task.WaitAsync(Wait, cancellationToken);
        Assert.Equal(TestParentState.Draft, (await LoadAsync(id, cancellationToken))!.State);
    }

    [Fact]
    public async Task A_step_with_a_comment_prompt_asks_for_the_comment_first_and_a_closed_prompt_runs_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, RolePermission.SettingsEdit);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        dialog.Step("test-parent-cancel").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Cancel the parent", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        Assert.Contains("Why is the parent canceled?", dialog.Host.Find(".prompt-dialog__message").TextContent, StringComparison.Ordinal);
        dialog.ConfirmButton("Cancel").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal(TestParentState.Active, (await LoadAsync(id, cancellationToken))!.State);

        dialog.Step("test-parent-cancel").Click();
        dialog.Host.WaitForElement(".form-dialog textarea").Change("No longer needed");
        dialog.ConfirmButton("Canceled").Click();

        await WaitForStateAsync(id, TestParentState.Canceled, cancellationToken);

        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.StepKeys()), Wait);
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task The_edits_are_saved_through_the_updater_before_the_step_and_a_refused_save_shows_why_and_keeps_them()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, RolePermission.SettingsEdit);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        await UpdateParentAsync(id, parent => parent.Notes = "Their note", cancellationToken);
        dialog.Host.Find("[name=test-parent-name]").Change("Renamed");
        dialog.Step("test-parent-activate").Click();
        await WaitForStateAsync(id, TestParentState.Active, cancellationToken);
        var saved = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Renamed", "Their note"), (saved!.Name, saved.Notes));
        dialog.Host.WaitForAssertion(() => Assert.Equal(["test-parent-cancel"], dialog.StepKeys()), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Their note", dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value")), Wait);

        await MoveAsync(scenario, id, "test-parent-cancel", cancellationToken, comment: "Canceled by someone else");
        dialog.Host.Find("[name=test-parent-notes]").Change("My note");
        dialog.Step("test-parent-cancel").Click();
        dialog.Host.WaitForElement(".form-dialog textarea").Change("Done with it");
        dialog.ConfirmButton("Canceled").Click();

        dialog.Host.WaitForAssertion(() => Assert.Contains("Notes cannot be changed while the TestParent is Canceled.", dialog.Alerts(), StringComparison.Ordinal), Wait);
        Assert.Equal("My note", dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value"));
        Assert.True(dialog.Context().HasChanges);
        Assert.Equal("Their note", (await LoadAsync(id, cancellationToken))!.Notes);
    }

    [Fact]
    public async Task A_step_whose_action_fails_stores_neither_the_edits_nor_the_step_and_the_form_keeps_the_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        dialog.Host.Find("[name=test-parent-name]").Change("Renamed");
        dialog.Host.Find("[name=test-parent-notes]").Change(TestParent.FailInAnAction);
        dialog.Step("test-parent-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("The step 'Active' could not be completed, so nothing was changed.", dialog.Alerts(), StringComparison.Ordinal), Wait);

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Parent", (string?)null, TestParentState.Draft), (stored!.Name, stored.Notes, stored.State));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal(("Renamed", TestParent.FailInAnAction), (dialog.Host.Find("[name=test-parent-name]").GetAttribute("value"), dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value")));
        Assert.True(context.HasChanges);
        Assert.Equal(new StateEntityDialogResult(false, false, id), context.DialogResult);
        Assert.Equal(1, context.Version);
    }

    [Fact]
    public async Task A_self_transition_whose_action_fails_does_not_store_the_edit()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParent.FilePermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        dialog.Host.Find("[name=test-parent-name]").Change("Renamed");
        dialog.Host.Find("[name=test-parent-notes]").Change(TestParent.FailInAnAction);
        dialog.Step("test-parent-file").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("The step 'File' could not be completed, so nothing was changed.", dialog.Alerts(), StringComparison.Ordinal), Wait);

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(("Parent", (string?)null), (stored!.Name, stored.Notes));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.True(dialog.Context().HasChanges);
    }

    [Fact]
    public async Task A_change_someone_else_made_that_blocks_the_step_refuses_it_with_the_servers_reason_and_stores_neither_the_edit_nor_the_step()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        await ClearNameAsync(id, cancellationToken);
        dialog.Host.Find("[name=test-parent-notes]").Change("My note");
        dialog.WaitForEdit();
        Assert.Null(dialog.Step("test-parent-activate").GetAttribute("aria-disabled"));

        dialog.Step("test-parent-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("Name is required", dialog.Alerts(), StringComparison.Ordinal), Wait);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((string.Empty, (string?)null, TestParentState.Draft), (stored!.Name, stored.Notes, stored.State));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal("My note", dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value"));
        Assert.True(context.HasChanges);
        Assert.False(context.HasSaved);
    }

    [Fact]
    public async Task A_permission_taken_away_after_the_footer_asked_refuses_the_step_and_stores_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsEdit);
        var id = await AddParentAsync(scenario.A, TestParentState.Active, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        Assert.Equal(["test-parent-cancel"], dialog.StepKeys());

        await using (var system = this.database.CreateSystemContext())
        {
            var roles = await system.Set<TenantRolePermission>().IgnoreQueryFilters().Where(permission => permission.TenantId == scenario.A.TenantId && permission.Permission == RolePermission.SettingsEdit).Select(permission => permission.TenantRoleId).ToListAsync(cancellationToken);
            await system.OrganizationUnitUserRoles.IgnoreQueryFilters().Where(holding => holding.TenantId == scenario.A.TenantId && holding.UserId == scenario.A.OwnerId && roles.Contains(holding.TenantRoleId)).ExecuteDeleteAsync(cancellationToken);
        }

        await circuit.Page.Services.GetRequiredService<PermissionService>().InvalidateCacheAsync(cancellationToken);
        dialog.Host.Find("[name=test-parent-notes]").Change("My note");
        dialog.Step("test-parent-cancel").Click();
        dialog.Host.WaitForElement(".form-dialog textarea").Change("Done with it");
        dialog.ConfirmButton("Canceled").Click();

        dialog.Host.WaitForAssertion(() => Assert.Contains("needs the permission", dialog.Alerts(), StringComparison.Ordinal), Wait);
        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal((TestParentState.Active, (string?)null), (stored!.State, stored.Notes));
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.Equal("My note", dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value"));
        Assert.True(dialog.Context().HasChanges);
    }

    [Fact]
    public async Task A_confirmed_step_does_not_start_while_another_step_that_started_during_its_confirmation_runs()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForElement(".form-dialog");
        circuit.Probe.HoldOtherReads();
        dialog.Step("test-parent-save").Click();
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);

        dialog.ConfirmButton("Deleted").Click();
        Assert.False(await circuit.Probe.WaitForHeldOtherReadsAsync(2, cancellationToken, TimeSpan.FromMilliseconds(500)));

        circuit.Probe.ReleaseOtherReads();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        Assert.NotNull(await LoadAsync(id, cancellationToken));
        Assert.Equal(["test-parent-save"], (await HistoryOfAsync(id, cancellationToken)).Select(entry => entry.TransitionKey));
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task After_a_step_the_footer_says_so_and_the_focus_goes_back_to_its_button_once_the_new_steps_are_drawn()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var module = circuit.Page.JSInterop.SetupModule(StateEntityDialogHeader.ModulePath);
        var dialog = await circuit.OpenLoadedAsync(id);

        var before = circuit.Probe.OtherParentReads;
        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", dialog.Host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Single(module.Invocations["restoreFocus"]), Wait);
        Assert.Equal("test-parent-save", module.Invocations["restoreFocus"].Single().Arguments[1]);
        var stepReads = circuit.Probe.OtherParentReads - before - 1;

        circuit.Probe.HoldOtherReads(skip: stepReads);
        dialog.Step("test-parent-save").Click();
        await circuit.Probe.WaitForHeldOtherReadsAsync(1, cancellationToken);
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        Assert.Single(module.Invocations["restoreFocus"]);
        circuit.Probe.ReleaseOtherReads();
        dialog.Host.WaitForAssertion(() => Assert.Equal(2, module.Invocations["restoreFocus"].Count), Wait);

        dialog.Step("test-parent-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Status()), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("test-parent-activate", module.Invocations["restoreFocus"].Last().Arguments[1]), Wait);

        await dialog.CloseByEscAsync();
        dialog.Host.WaitForAssertion(() => module.VerifyInvoke("releaseDialog"), Wait);
    }

    [Fact]
    public async Task The_layout_footer_and_buttons_have_names_roles_and_descriptions()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, string.Empty, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var module = circuit.Page.JSInterop.SetupModule(StateEntityDialogHeader.ModulePath);
        var dialog = await circuit.OpenLoadedAsync(id);

        var element = dialog.Host.Find("[role=dialog]");
        Assert.Equal("Parent", element.GetAttribute("aria-label"));
        var heading = dialog.Host.Find(".state-entity-dialog-header__title");
        Assert.Equal("H2", heading.TagName);
        Assert.Equal("-1", heading.GetAttribute("tabindex"));
        Assert.StartsWith("state-entity-dialog-title-", heading.Id, StringComparison.Ordinal);
        module.VerifyInvoke("nameDialog");

        Assert.Equal("Close", dialog.Host.Find(".state-entity-dialog-header__close").GetAttribute("aria-label"));
        var bar = dialog.Host.Find(".state-entity-dialog-footer__bar");
        Assert.Equal(("group", "Steps"), (bar.GetAttribute("role"), bar.GetAttribute("aria-label")));
        Assert.All(bar.QuerySelectorAll("button"), button => Assert.False(string.IsNullOrWhiteSpace(button.TextContent)));

        foreach (var button in bar.QuerySelectorAll("button"))
        {
            var card = dialog.Host.Find($"#{button.GetAttribute("aria-describedby")}");
            Assert.Equal("tooltip", card.GetAttribute("role"));
            Assert.False(string.IsNullOrWhiteSpace(card.TextContent));
        }

        Assert.Equal(("status", "polite"), (dialog.Host.Find(".state-entity-dialog-footer__status").GetAttribute("role"), dialog.Host.Find(".state-entity-dialog-footer__status").GetAttribute("aria-live")));

        Assert.Single(dialog.Host.FindAll("input[name=test-parent-name]"));
    }

    [Fact]
    public async Task An_edit_reaches_the_header_and_the_footer_without_asking_the_server()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();
        var reads = circuit.Probe.OtherParentReads;
        Assert.Empty(dialog.Host.FindAll(".state-entity-dialog-header__unsaved"));
        Assert.Equal(["test-parent-delete", "test-parent-save", "test-parent-activate"], dialog.StepKeys());

        dialog.Host.Find("[name=test-parent-notes]").Change(TestParent.AsksForAComment);
        dialog.WaitForEdit();

        Assert.Equal("Unsaved changes", dialog.Host.Find(".state-entity-dialog-header__unsaved").TextContent);
        Assert.Equal(["test-parent-delete", "test-parent-save", "test-parent-annotate", "test-parent-activate"], dialog.StepKeys());
        Assert.Equal((1, 1), (context.Version, context.OfferRequests));
        Assert.Equal(reads, circuit.Probe.OtherParentReads);
    }

    [Fact]
    public async Task Clearing_a_required_field_takes_the_save_away_and_a_save_clicked_anyway_writes_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();
        var writes = circuit.Probe.ParentWrites;

        dialog.Host.Find("[name=test-parent-name]").Change(string.Empty);
        dialog.WaitForEdit();
        Assert.Equal(["test-parent-delete", "test-parent-activate"], dialog.StepKeys());
        Assert.Equal("true", dialog.Step("test-parent-activate").GetAttribute("aria-disabled"));

        var attempt = await dialog.Host.InvokeAsync(() => context.RunTransitionAsync(context.Workflow.GetTransition("test-parent-save"), null));
        Assert.Equal("Name is required", attempt.Error);
        Assert.Equal(writes, circuit.Probe.ParentWrites);
        Assert.Equal("Parent", (await LoadAsync(id, cancellationToken))!.Name);

        dialog.Host.Find("[name=test-parent-name]").Change("Parent again");
        dialog.Host.WaitForAssertion(() => Assert.Contains("test-parent-save", dialog.StepKeys()), Wait);
        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        Assert.Equal("Parent again", (await LoadAsync(id, cancellationToken))!.Name);
    }

    [Fact]
    public async Task Typing_the_missing_value_offers_the_steps_it_unblocks_without_a_save_and_repairs_a_stored_draft()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, string.Empty, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();
        Assert.Equal(["test-parent-delete", "test-parent-activate"], dialog.StepKeys());
        Assert.Equal("true", dialog.Step("test-parent-activate").GetAttribute("aria-disabled"));
        var (reads, writes) = (circuit.Probe.OtherParentReads, circuit.Probe.ParentWrites);

        dialog.Host.Find("[name=test-parent-name]").Change("Named");
        dialog.WaitForEdit();
        Assert.Equal(["test-parent-delete", "test-parent-save", "test-parent-activate"], dialog.StepKeys());
        Assert.Null(dialog.Step("test-parent-activate").GetAttribute("aria-disabled"));
        Assert.Equal((1, reads, writes), (context.OfferRequests, circuit.Probe.OtherParentReads, circuit.Probe.ParentWrites));

        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        dialog.WaitUntilAsked(2);
        Assert.Equal("Named", (await LoadAsync(id, cancellationToken))!.Name);
        dialog.Step("test-parent-activate").Click();
        await WaitForStateAsync(id, TestParentState.Active, cancellationToken);
    }

    [Fact]
    public async Task The_service_refuses_a_save_that_empties_a_field_the_stored_state_makes_Required()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var named = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        var nameless = await AddParentAsync(scenario.A, TestParentState.Draft, string.Empty, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = host.Get<TestParentDialogService>();

        var updater = (await service.LoadTestParentUpdaterAsync(named, cancellationToken)).Value!;
        updater.Entity.Name = "  ";
        updater.Entity.Notes = "A note";
        var refused = await service.UpdateTestParentAsync(updater, cancellationToken);
        Assert.Equal((false, "Name is required while the TestParent is Draft."), (refused.IsSuccess, refused.Error));
        Assert.Equal(("Parent", (string?)null), ((await LoadAsync(named, cancellationToken))!.Name, (await LoadAsync(named, cancellationToken))!.Notes));

        var repair = (await service.LoadTestParentUpdaterAsync(nameless, cancellationToken)).Value!;
        repair.Entity.Notes = "Kept";
        Assert.True((await service.UpdateTestParentAsync(repair, cancellationToken)).IsSuccess);
        repair.Entity.Name = "Named";
        Assert.True((await service.UpdateTestParentAsync(repair, cancellationToken)).IsSuccess);
        Assert.Equal(("Named", "Kept"), ((await LoadAsync(nameless, cancellationToken))!.Name, (await LoadAsync(nameless, cancellationToken))!.Notes));
    }

    [Fact]
    public async Task An_edit_of_the_entitys_id_never_sends_a_step_to_another_row()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        var other = await AddParentAsync(scenario.A, TestParentState.Draft, "Other", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        await dialog.Host.InvokeAsync(() =>
        {
            context.Entity!.Id = other;
            context.Entity.Name = "Moved";
            context.NotifyEdited();
        });
        var attempt = await dialog.Host.InvokeAsync(() => context.RunTransitionAsync(context.Workflow.GetTransition("test-parent-save"), null));

        Assert.Equal("The TestParent in this dialog no longer matches the record it opened. Close the dialog and open it again.", attempt.Error);
        Assert.Equal(id, context.EntityId);
        Assert.Equal(("Parent", "Other"), ((await LoadAsync(id, cancellationToken))!.Name, (await LoadAsync(other, cancellationToken))!.Name));
        Assert.Empty(await HistoryOfAsync(other, cancellationToken));
    }

    [Fact]
    public void The_reload_that_drops_edits_is_the_contexts_own()
    {
        var reload = typeof(StateEntityDialogContext).GetMethod(nameof(StateEntityDialogContext.ReloadEntityAsync), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;

        Assert.True(reload.IsAssembly);
    }

    [Fact]
    public async Task Before_the_first_save_only_the_saves_are_offered_and_the_first_save_creates_the_row()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenCreateAsync();
        dialog.Host.WaitForElement("[name=test-parent-name]");
        var context = dialog.Context();

        Assert.Equal("New parent", dialog.Title());
        dialog.Host.WaitForAssertion(() => Assert.Empty(circuit.UrlState.DialogStack), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", dialog.Host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        Assert.Empty(dialog.StepKeys());
        dialog.Host.Find("[name=test-parent-name]").Change("Created");
        dialog.WaitForEdit();
        Assert.Equal(["test-parent-save"], dialog.StepKeys());
        Assert.Equal((0, 1), (circuit.Probe.OtherParentReads, context.OfferRequests));

        dialog.Step("test-parent-save").Click();

        dialog.Host.WaitForAssertion(() => Assert.Equal(["test-parent-delete", "test-parent-save", "test-parent-activate"], dialog.StepKeys()), Wait);
        var id = Assert.IsType<Guid>(context.EntityId);
        Assert.Equal(id, context.Updater!.Entity.Id);
        Assert.Same(context.Updater.Entity, context.Entity);
        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, id)], circuit.UrlState.DialogStack);
        Assert.Equal(("Created", TestParentState.Draft), ((await LoadAsync(id, cancellationToken))!.Name, (await LoadAsync(id, cancellationToken))!.State));
        Assert.Equal(["test-parent-save"], (await HistoryOfAsync(id, cancellationToken)).Select(entry => entry.TransitionKey));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Created", dialog.Title()), Wait);

        Assert.Contains(TestParentDialogService.CreatedMessage, dialog.Host.Find(".state-entity-dialog-footer__notice").TextContent, StringComparison.Ordinal);
        dialog.Host.Find("[name=test-parent-notes]").Change("A note");
        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("A note", dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".state-entity-dialog-footer__notice")), Wait);

        var until = DateTime.UtcNow + Wait;
        while ((await LoadAsync(id, cancellationToken))!.Notes != "A note")
        {
            Assert.True(DateTime.UtcNow < until, "The save never stored the note.");
            await Task.Delay(50, cancellationToken);
        }
    }

    [Fact]
    public async Task A_create_whose_step_is_refused_stores_nothing_and_stays_in_create_mode_with_the_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParent.FilePermission);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenCreateAsync();
        dialog.Host.WaitForElement("[name=test-parent-name]");
        var context = dialog.Context();
        var name = $"Refused {Guid.NewGuid():N}";

        dialog.Host.Find("[name=test-parent-name]").Change(name);
        dialog.Host.Find("[name=test-parent-notes]").Change(TestParent.FailInAnAction);
        dialog.Step("test-parent-file").Click();

        dialog.Host.WaitForAssertion(() => Assert.Contains("The step 'File' could not be completed, so nothing was changed.", dialog.Alerts(), StringComparison.Ordinal), Wait);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.False(await system.TestParents.AnyAsync(row => row.Name == name, cancellationToken));
        }

        Assert.Null(context.EntityId);
        Assert.Null(context.Updater);
        Assert.Equal("New parent", dialog.Title());
        Assert.Equal((name, TestParent.FailInAnAction), (dialog.Host.Find("[name=test-parent-name]").GetAttribute("value"), dialog.Host.Find("[name=test-parent-notes]").GetAttribute("value")));
        Assert.Empty(circuit.UrlState.DialogStack);
        Assert.False(context.HasSaved);
    }

    [Fact]
    public async Task Before_the_first_save_only_the_steps_the_access_gate_lets_the_user_take_are_offered()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, TestParentDialogService.ViewPermission, TestParent.FilePermission);

        await using (var circuit = Circuit(scenario.A.Runtime()))
        {
            var owners = await circuit.OpenCreateAsync();
            owners.Host.WaitForElement("[name=test-parent-name]").Change("New");
            owners.WaitForEdit();
            Assert.Equal(["test-parent-save"], owners.StepKeys());
        }

        await using var holders = Circuit(scenario.A.RuntimeOf(holder));
        var dialog = await holders.OpenCreateAsync();
        dialog.Host.WaitForElement("[name=test-parent-name]").Change("New");
        dialog.WaitForEdit();
        Assert.Equal(["test-parent-save", "test-parent-file"], dialog.StepKeys());
    }

    [Fact]
    public async Task Before_the_first_save_the_platform_console_is_offered_no_step_for_a_tenants_record_and_an_impersonating_owner_the_users_own()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await using (var circuit = Circuit(platform.PlatformOwnerRuntime()))
        {
            var console = await circuit.OpenCreateAsync();
            console.Host.WaitForElement("[name=test-parent-name]").Change("New");
            console.WaitForEdit();
            Assert.Empty(console.StepKeys());
        }

        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        await using var tenants = Circuit(impersonating);
        var dialog = await tenants.OpenCreateAsync();
        dialog.Host.WaitForElement("[name=test-parent-name]").Change("New");
        dialog.WaitForEdit();
        Assert.Equal(["test-parent-save"], dialog.StepKeys());
    }

    [Fact]
    public async Task Before_the_first_save_a_step_that_asks_a_comment_is_never_offered()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var saved = await circuit.OpenLoadedAsync(id);
        saved.Host.Find("[name=test-parent-notes]").Change(TestParent.AsksForAComment);
        saved.WaitForEdit();
        Assert.Contains("test-parent-annotate", saved.StepKeys());
        _ = saved.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        saved.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", saved.Host.Find(".form-dialog-header__title").TextContent), Wait);
        saved.ConfirmButton("Discard changes").Click();
        await saved.Result.WaitAsync(Wait, cancellationToken);

        var created = await circuit.OpenCreateAsync();
        created.Host.WaitForElement("[name=test-parent-name]").Change("New");
        created.Host.Find("[name=test-parent-notes]").Change(TestParent.AsksForAComment);
        created.WaitForEdit();
        Assert.Equal(["test-parent-save"], created.StepKeys());
    }

    [Fact]
    public async Task Before_the_first_save_a_condition_that_throws_blocks_its_step_with_a_plain_reason_and_creates_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenCreateAsync();
        var context = dialog.Context();
        var name = $"Thrown {Guid.NewGuid():N}";

        dialog.Host.WaitForElement("[name=test-parent-name]").Change(name);
        dialog.Host.Find("[name=test-parent-notes]").Change(TestParent.FailInTheSavesCondition);
        dialog.WaitForEdit();
        Assert.Empty(dialog.StepKeys());

        var attempt = await dialog.Host.InvokeAsync(() => context.RunTransitionAsync(context.Workflow.GetTransition("test-parent-save"), null));
        Assert.Equal(StateEntityDialogContext.CannotBeChecked, attempt.Error);
        Assert.DoesNotContain("The save's condition failed.", dialog.Host.Markup, StringComparison.Ordinal);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.AnyAsync(row => row.Name == name, cancellationToken));
    }

    [Fact]
    public async Task Before_the_first_save_a_child_row_added_counts_as_an_edit_and_closing_asks()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenCreateAsync();
        dialog.Host.WaitForElement("[name=test-parent-name]");
        var context = dialog.Context();
        Assert.False(context.HasChanges);

        await dialog.Host.InvokeAsync(() =>
        {
            context.Entity!.Lines.Add(new TestParentLine { Code = "L1", Text = "A line" });
            context.NotifyEdited();
        });

        Assert.True(context.HasChanges);
        dialog.WaitForEdit();
        Assert.Equal("Unsaved changes", dialog.Host.Find(".state-entity-dialog-header__unsaved").TextContent);
        _ = dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        Assert.False((await dialog.Result.WaitAsync(Wait, cancellationToken)).Changed);
    }

    [Fact]
    public async Task Before_the_first_save_a_child_row_replaced_by_another_with_the_same_values_counts_as_an_edit()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        circuit.Service.NewLines = ["L1"];
        var dialog = await circuit.OpenCreateAsync();
        var context = dialog.Context();
        Assert.False(context.HasChanges);

        await dialog.Host.InvokeAsync(() =>
        {
            context.Entity!.Lines.Clear();
            context.Entity.Lines.Add(new TestParentLine { Code = "L1", Text = "L1" });
            context.NotifyEdited();
        });

        Assert.True(context.HasChanges);
    }

    [Fact]
    public void Closing_the_dialog_drops_a_message_left_to_show_once()
    {
        var showOnce = typeof(StateEntityDialogContext).GetMethod("ShowOnce", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var kept = new TestParentDialogContext(null!, Guid.NewGuid());
        var closed = new TestParentDialogContext(null!, Guid.NewGuid());
        showOnce.Invoke(kept, ["The initial password"]);
        showOnce.Invoke(closed, ["The initial password"]);

        closed.End();

        Assert.Equal("The initial password", kept.TakeOneTimeMessage());
        Assert.Null(closed.TakeOneTimeMessage());
    }

    [Fact]
    public async Task A_move_by_the_foreign_key_is_left_to_the_server_which_judges_the_step_on_the_row_the_move_makes_and_stores_nothing_when_it_refuses()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var open = await AddParentAsync(scenario.A, TestParentState.Draft, "Open", cancellationToken);
        var closed = await AddParentAsync(scenario.A, TestParentState.Draft, "Closed", cancellationToken);
        await UpdateParentAsync(closed, parent => parent.Notes = TestParent.TakesNoActiveChildren, cancellationToken);
        var child = await AddChildAsync(scenario.A, open, "Child", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenChildLoadedAsync(child);
        Assert.Null(dialog.Step("test-child-activate").GetAttribute("aria-disabled"));

        await dialog.Host.InvokeAsync(() =>
        {
            var context = dialog.AnyContext<TestChildState, TestChild>();
            context.Entity!.ParentId = closed;
            context.NotifyEdited();
        });
        dialog.WaitForEdit();
        Assert.Null(dialog.Step("test-child-activate").GetAttribute("aria-disabled"));

        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("The parent takes active children", dialog.Alerts(), StringComparison.Ordinal), Wait);
        await using var system = this.database.CreateSystemContext();
        var stored = await system.TestChildren.AsNoTracking().SingleAsync(row => row.Id == child, cancellationToken);
        Assert.Equal((open, TestChildState.Draft), (stored.ParentId, stored.State));
        Assert.Empty(await HistoryOfAsync(child, cancellationToken));
        Assert.True(dialog.AnyContext<TestChildState, TestChild>().HasChanges);
    }

    [Fact]
    public async Task A_condition_on_a_navigation_the_graph_did_not_load_is_left_to_the_server_however_the_form_is_edited()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var parent = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        var child = await AddChildAsync(scenario.A, parent, "Child", cancellationToken, grandchild: TestGrandchild.HoldsItsChildOpen);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenChildLoadedAsync(child);

        Assert.Contains("test-child-activate", dialog.StepKeys());
        Assert.DoesNotContain("test-child-cancel-draft", dialog.StepKeys());

        dialog.Host.Find("[name=test-child-name]").Change("Renamed");
        dialog.WaitForEdit();
        Assert.True(dialog.AnyContext<TestChildState, TestChild>().HasChanges);
        Assert.Contains("test-child-activate", dialog.StepKeys());
        Assert.DoesNotContain("test-child-cancel-draft", dialog.StepKeys());
    }

    [Fact]
    public void In_memory_a_saved_entity_holds_only_the_navigations_its_graph_included()
    {
        var parent = new TestParent { Id = Guid.NewGuid() };
        var child = new TestChild { Id = Guid.NewGuid(), Parent = parent, ParentId = parent.Id };
        parent.Children.Add(child);
        var graph = new HashSet<string>(StringComparer.Ordinal) { "Children" };

        var state = new DialogNavigationLoadState(parent, graph);
        Assert.True(state.IsLoaded(parent, nameof(TestParent.Children)));
        Assert.False(state.MissedAny);

        Assert.False(new DialogNavigationLoadState(parent, graph).IsLoaded(parent, nameof(TestParent.Lines)));
        Assert.False(new DialogNavigationLoadState(parent, graph).IsLoaded(child, nameof(TestChild.Grandchildren)));
        Assert.False(new DialogNavigationLoadState(parent, graph).IsLoaded(new TestChild(), nameof(TestChild.Grandchildren)));
        var missed = new DialogNavigationLoadState(parent, graph);
        missed.IsLoaded(parent, nameof(TestParent.Lines));
        Assert.True(missed.MissedAny);

        Assert.True(new DialogNavigationLoadState(parent, new HashSet<string>(StringComparer.Ordinal) { "Children", "Children.Grandchildren" }).IsLoaded(child, nameof(TestChild.Grandchildren)));
    }

    [Fact]
    public void In_memory_a_reference_without_a_foreign_key_property_is_held_as_it_is()
    {
        var holder = new KeylessHolder { Owner = new TestParent { Id = Guid.NewGuid() } };

        var built = new DialogNavigationLoadState();
        Assert.True(built.IsLoaded(holder, nameof(KeylessHolder.Owner)));
        Assert.False(built.MissedAny);

        var loaded = new DialogNavigationLoadState(holder, new HashSet<string>(StringComparer.Ordinal) { nameof(KeylessHolder.Owner) });
        Assert.True(loaded.IsLoaded(holder, nameof(KeylessHolder.Owner)));
        Assert.False(loaded.MissedAny);
    }

    [Fact]
    public void The_graph_paths_are_read_from_the_query_the_graph_builds()
    {
        using var context = this.database.CreateSystemContext();

        Assert.Equal(["Attributes", "Children", "Children.Grandchildren"], WorkflowGraphPaths.Of<TestParent>(context, TestParentWorkflowQueries.WithWorkflowGraph).Order(StringComparer.Ordinal));
        Assert.Equal(["Parent"], WorkflowGraphPaths.Of<TestChild>(context, TestChildWorkflowQueries.WithWorkflowGraph));
        Assert.Equal(["Lines"], WorkflowGraphPaths.Of<TestParent>(context, query => query.Include(parent => parent.Lines.Where(line => line.Code != "x"))));
        Assert.Equal(["Children", "Children.Grandchildren"], WorkflowGraphPaths.Of<TestParent>(context, query => query.Include("Children.Grandchildren")).Order(StringComparer.Ordinal));
        Assert.Empty(WorkflowGraphPaths.Of<TestParent>(context, null));
    }

    [Fact]
    public async Task A_create_judges_a_reference_named_only_by_its_foreign_key_by_the_row_it_names()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var draft = await AddParentAsync(scenario.A, TestParentState.Draft, "In use", cancellationToken);
        var canceled = await AddParentAsync(scenario.A, TestParentState.Canceled, "Canceled", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();

        var refused = await service.CreateAndTransitionAsync<TestChildState, TestChild>(new TestChild { ParentId = canceled, Name = "Child" }, "test-child-activate", cancellationToken: cancellationToken);
        var created = await service.CreateAndTransitionAsync<TestChildState, TestChild>(new TestChild { ParentId = draft, Name = "Child" }, "test-child-activate", cancellationToken: cancellationToken);

        Assert.Equal((false, "The parent is not canceled"), (refused.IsSuccess, refused.Error));
        Assert.True(created.IsSuccess, created.Error);
        await using var system = this.database.CreateSystemContext();
        var children = await system.TestChildren.AsNoTracking().Where(row => row.ParentId == draft || row.ParentId == canceled).ToListAsync(cancellationToken);
        Assert.Equal([(created.Value, draft, TestChildState.Active)], children.Select(row => (row.Id, row.ParentId, row.State)));
    }

    [Fact]
    public async Task A_refused_create_leaves_the_callers_instance_as_it_was_so_it_can_be_sent_again()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var draft = await AddParentAsync(scenario.A, TestParentState.Draft, "In use", cancellationToken);
        var canceled = await AddParentAsync(scenario.A, TestParentState.Canceled, "Canceled", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();

        var child = new TestChild { ParentId = canceled, Name = "Retried" };
        var refused = await service.CreateAndTransitionAsync<TestChildState, TestChild>(child, "test-child-activate", cancellationToken: cancellationToken);
        Assert.Equal((false, "The parent is not canceled"), (refused.IsSuccess, refused.Error));

        Assert.Equal((Guid.Empty, Guid.Empty, Guid.Empty, (TestParent?)null), (child.Id, child.TenantId, child.CreatedById, child.Parent));

        child.ParentId = draft;
        var created = await service.CreateAndTransitionAsync<TestChildState, TestChild>(child, "test-child-activate", cancellationToken: cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        Assert.NotEqual(Guid.Empty, created.Value);
        Assert.Equal(Guid.Empty, child.Id);
        await using var system = this.database.CreateSystemContext();
        var rows = await system.TestChildren.AsNoTracking().Where(row => row.ParentId == draft || row.ParentId == canceled).ToListAsync(cancellationToken);
        Assert.Equal([(created.Value, draft, TestChildState.Active)], rows.Select(row => (row.Id, row.ParentId, row.State)));
    }

    [Fact]
    public async Task A_create_naming_a_missing_or_another_tenants_row_is_refused_as_not_found_alike()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var theirs = await AddParentAsync(scenario.B, TestParentState.Draft, "Company B parent", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();

        var missing = await service.CreateAndTransitionAsync<TestChildState, TestChild>(new TestChild { ParentId = Guid.NewGuid(), Name = "Missing" }, "test-child-activate", cancellationToken: cancellationToken);
        var foreign = await service.CreateAndTransitionAsync<TestChildState, TestChild>(new TestChild { ParentId = theirs, Name = "Foreign" }, "test-child-activate", cancellationToken: cancellationToken);

        Assert.Equal((false, "The TestParent was not found."), (missing.IsSuccess, missing.Error));
        Assert.Equal((false, "The TestParent was not found."), (foreign.IsSuccess, foreign.Error));
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestChildren.IgnoreQueryFilters().AnyAsync(row => row.ParentId == theirs, cancellationToken));
    }

    [Fact]
    public async Task A_create_whose_first_step_takes_child_records_along_or_does_not_leave_the_entry_state_is_refused()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();
        var name = $"Refused {Guid.NewGuid():N}";

        var cascading = await service.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = name }, "test-parent-delete", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Equal((false, "The step 'Deleted' takes child records along, so it cannot create a TestParent."), (cascading.IsSuccess, cascading.Error));

        var later = await service.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = name, State = TestParentState.Canceled }, "test-parent-reopen", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Equal((false, "A new TestParent starts in its entry state, and 'Draft' does not leave it."), (later.IsSuccess, later.Error));

        var systemStep = await service.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = name, State = TestParentState.Canceled }, "test-parent-restore", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Equal((false, WorkflowService.SystemDrivenRefusal("Draft")), (systemStep.IsSuccess, systemStep.Error));

        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.TestParents.IgnoreQueryFilters().AnyAsync(row => row.Name == name, cancellationToken));
    }

    [Fact]
    public async Task The_platform_console_creates_a_tenants_record_only_while_impersonating_one_of_its_users()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var parentA = await AddParentAsync(scenario.A, TestParentState.Draft, "A parent", cancellationToken);
        var name = $"Console {Guid.NewGuid():N}";
        const string outsideATenant = "A TestParent belongs to a tenant: the platform console creates one only while impersonating a user of that tenant.";

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var service = console.Get<WorkflowService>();

            var plain = await service.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = name }, "test-parent-save", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            Assert.Equal((false, outsideATenant), (plain.IsSuccess, plain.Error));
            var named = await service.CreateAndTransitionAsync<TestChildState, TestChild>(new TestChild { ParentId = parentA, Name = name, TenantId = scenario.A.TenantId }, "test-child-activate", cancellationToken: cancellationToken);
            Assert.Equal((false, "A TestChild belongs to a tenant: the platform console creates one only while impersonating a user of that tenant."), (named.IsSuccess, named.Error));

            var later = await service.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = name, State = TestParentState.Canceled }, "test-parent-reopen", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            Assert.Equal((false, "A new TestParent starts in its entry state, and 'Draft' does not leave it."), (later.IsSuccess, later.Error));

            var access = await service.GetTransitionAccessAsync<TestParentState, TestParent>(TestParentState.Draft, cancellationToken);
            Assert.True(access.IsSuccess, access.Error);
            Assert.All(access.Value!, answer => Assert.False(answer.MayRun));
        }

        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        await using (var tenant = ServiceHost.Create(this.database, impersonating))
        {
            var created = await tenant.Get<WorkflowService>().CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = name }, "test-parent-save", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal([scenario.A.TenantId], await system.TestParents.IgnoreQueryFilters().Where(row => row.Name == name).Select(row => row.TenantId).ToListAsync(cancellationToken));
        Assert.False(await system.TestChildren.IgnoreQueryFilters().AnyAsync(row => row.Name == name, cancellationToken));
    }

    [Fact]
    public async Task Only_a_signed_in_user_is_told_which_steps_there_are()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var service = host.Get<WorkflowService>();

        var access = await service.GetTransitionAccessAsync<TestParentState, TestParent>(TestParentState.Draft, cancellationToken);
        var available = await service.GetAvailableTransitionsAsync<TestParentState, TestParent>(id, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken);

        Assert.Equal((false, "Only a signed-in user runs a workflow step."), (access.IsSuccess, access.Error));
        Assert.Equal((false, "Only a signed-in user runs a workflow step."), (available.IsSuccess, available.Error));
    }

    [Fact]
    public async Task A_step_with_edits_is_refused_before_anything_is_read_when_it_leaves_another_state_or_removes_the_row()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = host.Get<WorkflowService>();
        var updater = (await host.Get<TestParentDialogService>().LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        updater.Entity.Notes = "A note";

        var stale = await service.SaveAndTransitionAsync<TestParentState, TestParent>(updater, "test-parent-cancel", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        var delete = await service.SaveAndTransitionAsync<TestParentState, TestParent>(updater, "test-parent-delete", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);

        Assert.Equal((false, "The TestParent is Draft, but 'test-parent-cancel' leaves Active. Reload it and try again."), (stale.IsSuccess, stale.Error));
        Assert.Equal((false, "The step 'Deleted' removes the TestParent as it is stored, so it runs without the edits."), (delete.IsSuccess, delete.Error));
        Assert.Null((await LoadAsync(id, cancellationToken))!.Notes);
        Assert.True(updater.HasChanges());

        var saved = await service.SaveAndTransitionAsync<TestParentState, TestParent>(updater, "test-parent-save", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.True(saved.IsSuccess, saved.Error);
        Assert.False(updater.HasChanges());
        Assert.Equal("A note", (await LoadAsync(id, cancellationToken))!.Notes);
        Assert.Equal(["test-parent-save"], (await HistoryOfAsync(id, cancellationToken)).Select(entry => entry.TransitionKey));
    }

    [Fact]
    public void In_memory_a_reference_counts_as_loaded_only_when_it_agrees_with_its_foreign_key()
    {
        var parent = new TestParent { Id = Guid.NewGuid() };

        Assert.True(IsLoaded(new TestChild { Parent = parent, ParentId = parent.Id }));
        Assert.False(IsLoaded(new TestChild { ParentId = parent.Id }));
        Assert.False(IsLoaded(new TestChild { Parent = parent, ParentId = Guid.NewGuid() }));
        Assert.True(IsLoaded(new TestChild()));

        var collections = new DialogNavigationLoadState();
        Assert.True(collections.IsLoaded(new TestChild(), nameof(TestChild.Grandchildren)));
        Assert.False(collections.MissedAny);

        static bool IsLoaded(TestChild child)
        {
            var state = new DialogNavigationLoadState();
            var loaded = state.IsLoaded(child, nameof(TestChild.Parent));
            Assert.Equal(!loaded, state.MissedAny);
            return loaded;
        }
    }

    private async Task<TwoTenantScenario> ScenarioAsync(CancellationToken cancellationToken, params RolePermission[] extra) =>
        await ScenarioAsync(cancellationToken, owners: true, extra);

    private async Task<TwoTenantScenario> ScenarioAsync(CancellationToken cancellationToken, bool owners, params RolePermission[] extra)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, [TestParentDialogService.ViewPermission, .. extra]);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, TestParentDialogService.ViewPermission);
        if (owners)
        {
            await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken);
            await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken);
        }

        return scenario;
    }

    private DialogCircuit Circuit(IRuntimeExecutionContext runtime, bool withPermissionService = true, TimeProvider? clock = null) => new(this.database, runtime, withPermissionService, clock);

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, TestParentState state, string name, CancellationToken cancellationToken, bool withChild = false)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = name, State = state };
        if (withChild)
        {
            parent.Children.Add(new TestChild { Name = "Child" });
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<Guid> AddChildAsync(ScenarioTenant tenant, Guid parent, string name, CancellationToken cancellationToken, string? grandchild = null)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a child as a transition left it");
        var child = new TestChild { ParentId = parent, Name = name };
        if (grandchild is not null)
        {
            child.Grandchildren.Add(new TestGrandchild { Name = grandchild });
        }

        writer.TestChildren.Add(child);
        await writer.SaveChangesAsync(cancellationToken);
        return child.Id;
    }

    private sealed class KeylessHolder
    {
        public TestParent? Owner { get; set; }
    }

    private async Task UpdateParentAsync(Guid id, Action<TestParent> change, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var parent = await system.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
        change(parent);
        system.NameEditor(parent, parent.CreatedById);
        await system.SaveChangesAsync(cancellationToken);
    }

    private async Task ClearNameAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        await system.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, string.Empty), cancellationToken);
    }

    private async Task MoveAsync(TwoTenantScenario scenario, Guid id, string transitionKey, CancellationToken cancellationToken, string? comment = null)
    {
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, transitionKey, TestParentWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private async Task<TestParent?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
    }

    private async Task<List<Staffora.Core.Entities.Workflow.StateHistoryEntry>> HistoryOfAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == id).OrderBy(entry => entry.Created).ToListAsync(cancellationToken);
    }

    private async Task WaitForStateAsync(Guid id, TestParentState state, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + Wait;
        while ((await LoadAsync(id, cancellationToken))?.State != state)
        {
            Assert.True(DateTime.UtcNow < until, $"The parent never reached {state}.");
            await Task.Delay(50, cancellationToken);
        }
    }

    private sealed class DialogCircuit : IAsyncDisposable
    {
        private readonly ServiceHost host;

        public DialogCircuit(RealDatabase database, IRuntimeExecutionContext runtime, bool withPermissionService = true, TimeProvider? clock = null)
        {
            this.host = ServiceHost.Create(database, runtime, services => services.AddScoped<TestParentDialogService>().AddScoped<TestChildDialogService>().AddSingleton(clock ?? TimeProvider.System), [Probe]);
            Page = new PageContext();
            Page.Services.AddLogging();
            Page.Services.AddSingleton(runtime);
            Page.Services.AddSingleton(clock ?? TimeProvider.System);
            if (withPermissionService)
            {
                Page.Services.AddSingleton(this.host.Get<PermissionService>());
            }

            Page.Services.AddSingleton(this.host.Get<WorkflowService>());
            Page.Services.AddSingleton(this.host.Get<StateHistoryService>());
            Page.Services.AddSingleton(this.host.Get<TestParentDialogService>());
            Page.Services.AddSingleton(this.host.Get<TestChildDialogService>());
            Dialog = Page.Render<RadzenDialog>();
        }

        public CommandProbe Probe { get; } = new();

        public PageContext Page { get; }

        public IRenderedComponent<RadzenDialog> Dialog { get; }

        public DialogService Dialogs => Page.Services.GetRequiredService<DialogService>();

        public UrlStateService UrlState => Page.Services.GetRequiredService<UrlStateService>();

        public NavigationManager Navigation => Page.Services.GetRequiredService<NavigationManager>();

        public TestParentDialogService Service => Page.Services.GetRequiredService<TestParentDialogService>();

        public TestChildDialogService ChildService => Page.Services.GetRequiredService<TestChildDialogService>();

        public async Task<OpenDialog> OpenChildLoadedAsync(Guid id)
        {
            var before = OpenContexts();
            Task<StateEntityDialogResult>? result = null;
            await Dialog.InvokeAsync(() => { result = TestChildDialog.OpenAsync(Dialogs, ChildService, id); });
            WaitForNewDialog(before);
            Dialog.WaitForAssertion(() => Assert.Equal("loaded", Dialog.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
            Dialog.WaitForAssertion(() => Assert.Equal("false", Dialog.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
            return new OpenDialog(Dialog, result!);
        }

        public async Task<OpenDialog> OpenAsync(Guid id)
        {
            var before = OpenContexts();
            Task<StateEntityDialogResult>? result = null;
            await Dialog.InvokeAsync(() => { result = TestParentDialog.OpenAsync(Dialogs, Service, id); });
            WaitForNewDialog(before);
            return new OpenDialog(Dialog, result!);
        }

        private HashSet<StateEntityDialogContext> OpenContexts() =>
            [.. Dialog.FindComponents<StateEntityDialogLayout>().Select(layout => layout.Instance.Context)];

        private void WaitForNewDialog(HashSet<StateEntityDialogContext> before) =>
            Dialog.WaitForAssertion(() => Assert.Contains(Dialog.FindComponents<StateEntityDialogLayout>(), layout => !before.Contains(layout.Instance.Context)), Wait);

        public async Task<OpenDialog> OpenLoadedAsync(Guid id)
        {
            var dialog = await OpenAsync(id);
            Dialog.WaitForAssertion(() => Assert.Equal("loaded", Dialog.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
            Dialog.WaitForAssertion(() => Assert.Equal("false", Dialog.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
            return dialog;
        }

        public async Task<OpenDialog> OpenCreateAsync()
        {
            var before = OpenContexts();
            Task<StateEntityDialogResult>? result = null;
            await Dialog.InvokeAsync(() => { result = TestParentDialog.OpenCreateAsync(Dialogs, Service); });
            WaitForNewDialog(before);
            Dialog.WaitForAssertion(() => Assert.Equal("loaded", Dialog.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
            Dialog.WaitForAssertion(() => Assert.Equal("false", Dialog.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
            return new OpenDialog(Dialog, result!);
        }

        public async ValueTask DisposeAsync()
        {
            Probe.ReleaseLoads();
            Probe.ReleaseOtherReads();
            await Page.DisposeAsync();
            await this.host.DisposeAsync();
        }
    }

    private sealed record OpenDialog(IRenderedComponent<RadzenDialog> Host, Task<StateEntityDialogResult> Result)
    {
        public string Title() => Host.FindAll(".state-entity-dialog-header__title").Last().TextContent;

        public TestParentDialogContext Context() => (TestParentDialogContext)Host.FindComponents<StateEntityDialogLayout>().Last().Instance.Context;

        public StateEntityDialogContextBase<TState, TEntity> AnyContext<TState, TEntity>()
            where TState : struct, Enum
            where TEntity : class, Staffora.Core.Workflow.IStateEntity<TState> =>
            (StateEntityDialogContextBase<TState, TEntity>)Host.FindComponents<StateEntityDialogLayout>().Last().Instance.Context;

        public void WaitForEdit() => Host.WaitForAssertion(() => Assert.NotNull(Host.FindAll(".state-entity-dialog").Last().QuerySelector(".state-entity-dialog-header__unsaved")), Wait);

        public void WaitUntilAsked(int requests) => Host.WaitForAssertion(() =>
        {
            Assert.Equal(requests, Host.FindComponents<StateEntityDialogLayout>().Last().Instance.Context.OfferRequests);
            Assert.Equal("false", Host.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy"));
        }, Wait);

        public List<string> StepKeys() =>
            [.. Host.FindAll(".state-entity-dialog").Last().QuerySelectorAll("button[data-transition]").Select(button => button.GetAttribute("data-transition")!)];

        public IElement Step(string key) => Host.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{key}']")
            ?? throw new Xunit.Sdk.XunitException($"The step {key} is not drawn.");

        public string Alerts() => string.Join(" | ", Host.FindAll(".state-entity-dialog-footer__error[role=alert]").Select(alert => alert.TextContent));

        public string Status() => Host.Find(".state-entity-dialog-footer__status").TextContent;

        public IElement ConfirmButton(string text) =>
            Host.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

        public async Task CloseByEscAsync()
        {
            await Host.InvokeAsync(() => Host.Services.GetRequiredService<DialogService>().TryCloseFromJs(null));
            await Result.WaitAsync(Wait);
        }
    }

    private sealed class PageContext : WebTestContext;

    private sealed class CommandProbe : DbCommandInterceptor
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource> heldOtherReads = [];
        private int loads;
        private int otherParentReads;
        private int parentWrites;
        private int historyReads;
        private volatile TaskCompletionSource? hold;
        private int? holdOtherReadsAfter;

        public int Loads => Volatile.Read(ref this.loads);

        public int OtherParentReads => Volatile.Read(ref this.otherParentReads);

        public int HistoryReads => Volatile.Read(ref this.historyReads);

        public int ParentWrites => Volatile.Read(ref this.parentWrites);

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LoadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OtherReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool FailOtherReads;

        public void HoldLoads() => this.hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseLoads() => this.hold?.TrySetResult();

        public void HoldOtherReads(int skip = 0)
        {
            lock (this.gate)
            {
                this.holdOtherReadsAfter = OtherParentReads + skip;
            }
        }

        public void ReleaseOtherRead(int index)
        {
            lock (this.gate)
            {
                this.heldOtherReads[index].TrySetResult();
            }
        }

        public void ReleaseOtherReads()
        {
            lock (this.gate)
            {
                this.holdOtherReadsAfter = null;
                foreach (var held in this.heldOtherReads)
                {
                    held.TrySetResult();
                }
            }
        }

        public async Task<bool> WaitForHeldOtherReadsAsync(int count, CancellationToken cancellationToken, TimeSpan? within = null)
        {
            var until = DateTime.UtcNow + (within ?? Wait);
            while (true)
            {
                lock (this.gate)
                {
                    if (this.heldOtherReads.Count >= count)
                    {
                        return true;
                    }
                }

                if (DateTime.UtcNow > until)
                {
                    Assert.True(within is not null, $"{count} other reads were never held.");
                    return false;
                }

                await Task.Delay(20, cancellationToken);
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var kind = Count(command);
            if (kind == ReadKind.Other && this.FailOtherReads)
            {
                throw new InvalidOperationException("The probe failed this read.");
            }

            if (kind == ReadKind.Load && this.hold is { } hold)
            {
                LoadStarted.TrySetResult();
                try
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LoadCancelled.TrySetResult();
                    throw;
                }
            }
            else if (kind == ReadKind.Other && HeldOtherRead() is { } held)
            {
                try
                {
                    await held.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    OtherReadCancelled.TrySetResult();
                    throw;
                }
            }

            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Count(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }

        private TaskCompletionSource? HeldOtherRead()
        {
            lock (this.gate)
            {
                if (this.holdOtherReadsAfter is not { } after || OtherParentReads <= after)
                {
                    return null;
                }

                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.heldOtherReads.Add(held);
                return held;
            }
        }

        private ReadKind Count(DbCommand command)
        {
            var text = command.CommandText;
            if (text.Contains("FROM [StateHistoryEntries]", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref this.historyReads);
            }

            if (text.Contains(TestParentDialogService.LoadTag, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref this.loads);
                return ReadKind.Load;
            }

            if (text.Contains("UPDATE [TestParents]", StringComparison.Ordinal) || text.Contains("INSERT INTO [TestParents]", StringComparison.Ordinal) || text.Contains("DELETE FROM [TestParents]", StringComparison.Ordinal)
                || (text.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal) && text.Contains("FROM [TestParents]", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref this.parentWrites);
                return ReadKind.Write;
            }

            if (text.Contains("FROM [TestParents]", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref this.otherParentReads);
                return ReadKind.Other;
            }

            return ReadKind.None;
        }

        private enum ReadKind
        {
            None,
            Load,
            Other,
            Write,
        }
    }
}
