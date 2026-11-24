using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

public partial class StateEntityDialogTests
{
    [Fact]
    public async Task The_parents_children_tab_lists_its_children_and_a_row_opens_the_childs_own_dialog_on_top()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Afternoon group", cancellationToken);
        var morning = await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Equal([TestParentDialogContext.InfoView, TestParentDialogContext.ChildrenView], ViewKeys(dialog));
        Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog));

        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 2);
        Assert.Equal(["Afternoon group", "Morning group"], RowNames(dialog));
        Assert.All(dialog.Host.FindAll("[role=tabpanel][data-view=children] .list-state"), state => Assert.Equal("Draft", state.TextContent));

        var parent = dialog.Context();
        var queries = circuit.Service.ChildQueries;
        var child = await OpenRowAsync(circuit, dialog, morning);
        var layouts = dialog.Host.FindComponents<StateEntityDialogLayout>();
        Assert.Equal(2, layouts.Count);
        Assert.Same(parent, layouts[0].Instance.Context);
        Assert.Equal(morning, Assert.IsType<TestChildDialogContext>(layouts[1].Instance.Context).EntityId);
        Assert.Equal("Morning group", dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelector("[name=test-child-name]")!.GetAttribute("value"));
        Assert.Equal(2, dialog.Host.FindAll(".rz-dialog-wrapper").Count);
        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, id), new DialogStackSegment(TestChildDialogContext.Token, morning)], circuit.UrlState.DialogStack);

        var open = circuit.Page.Services.GetRequiredService<OpenStateEntityDialogs>();
        Assert.Same(child, open.Top);
        Assert.Equal(2, open.All.Count);

        var version = parent.Version;
        await CloseTopByEscAsync(dialog, layouts: 1);
        dialog.Host.WaitForAssertion(() => Assert.Equal(queries + 1, circuit.Service.ChildQueries), Wait);
        Assert.Equal(TestParentDialogContext.ChildrenView, SelectedView(dialog));
        Assert.Equal(version, parent.Version);
        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, id)], circuit.UrlState.DialogStack);
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task The_tabs_are_a_tablist_whose_selected_tab_alone_is_in_the_tab_order_and_names_its_panel()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        var tablist = dialog.Host.Find("[role=tablist]");
        Assert.Equal("Views", tablist.GetAttribute("aria-label"));
        var tabs = tablist.QuerySelectorAll("[role=tab]");
        Assert.Equal(["Info", "Children"], tabs.Select(tab => tab.QuerySelector(".state-entity-dialog-tabs__label")!.TextContent));
        Assert.All(tabs, tab => Assert.Empty(tab.QuerySelectorAll("span[aria-hidden]")));
        Assert.Equal(["true", "false"], tabs.Select(tab => tab.GetAttribute("aria-selected")));
        Assert.Equal(["0", "-1"], tabs.Select(tab => tab.GetAttribute("tabindex")));
        Assert.All(tabs, tab => Assert.Equal("button", tab.GetAttribute("type")));

        var info = dialog.Host.Find($"#{tabs[0].GetAttribute("aria-controls")}");
        Assert.Equal("tabpanel", info.GetAttribute("role"));
        Assert.Equal(tabs[0].Id, info.GetAttribute("aria-labelledby"));
        Assert.False(info.HasAttribute("hidden"));
        Assert.NotNull(info.QuerySelector("[name=test-parent-name]"));
        var notYet = dialog.Host.Find($"#{tabs[1].GetAttribute("aria-controls")}");
        Assert.True(notYet.HasAttribute("hidden"));
        Assert.Empty(notYet.Children);

        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Equal(["false", "true"], dialog.Host.FindAll("[role=tab]").Select(tab => tab.GetAttribute("aria-selected"))), Wait);
        Assert.Equal(["-1", "0"], dialog.Host.FindAll("[role=tab]").Select(tab => tab.GetAttribute("tabindex")));
        Assert.True(dialog.Host.Find("[role=tabpanel][data-view=info]").HasAttribute("hidden"));
        var children = dialog.Host.Find("[role=tabpanel][data-view=children]");
        Assert.False(children.HasAttribute("hidden"));
        Assert.Equal(dialog.Host.Find("[role=tab][data-view=children]").Id, children.GetAttribute("aria-labelledby"));
    }

    [Fact]
    public async Task A_hop_to_the_children_with_edits_saves_them_through_the_save_step_before_the_rows_are_read()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        string? notesWhenRead = null;
        circuit.Service.BeforeChildQuery = async token => notesWhenRead ??= (await LoadAsync(id, token))!.Notes;
        dialog.Host.Find("[name=test-parent-notes]").Change("Bring the projector");
        dialog.WaitForEdit();

        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);

        Assert.Equal("Bring the projector", notesWhenRead);
        Assert.Equal("Bring the projector", (await LoadAsync(id, cancellationToken))!.Notes);
        Assert.Equal("test-parent-save", Assert.Single(await HistoryOfAsync(id, cancellationToken)).TransitionKey);
        Assert.False(context.HasChanges);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));
    }

    [Fact]
    public async Task A_hop_with_edits_the_save_cannot_take_asks_and_keeping_them_stays_on_the_view_while_discarding_goes_on()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        dialog.Host.Find("[name=test-parent-name]").Change(string.Empty);
        dialog.WaitForEdit();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        Assert.Contains("Name is required", dialog.Host.Find(".form-dialog .dialog-notice__message").TextContent, StringComparison.Ordinal);

        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog));
        Assert.Equal(string.Empty, dialog.Host.Find("[name=test-parent-name]").GetAttribute("value"));
        Assert.True(context.HasChanges);
        Assert.Equal(0, circuit.Service.ChildQueries);
        Assert.Equal("Spring period job", (await LoadAsync(id, cancellationToken))!.Name);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));

        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(TestParentDialogContext.ChildrenView, SelectedView(dialog)), Wait);
        Assert.False(context.HasChanges);
        Assert.Equal("Spring period job", context.Entity!.Name);
        dialog.Host.WaitForAssertion(() => Assert.Equal(1, circuit.Service.ChildQueries), Wait);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_hop_whose_save_the_server_refuses_asks_with_its_reason_and_discarding_goes_on_with_the_stored_row()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();

        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestParents.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, TestParentState.Active), cancellationToken);
        }

        dialog.Host.Find("[name=test-parent-notes]").Change("Mine");
        dialog.WaitForEdit();
        SelectView(dialog, TestParentDialogContext.ChildrenView);

        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        var question = dialog.Host.Find(".form-dialog .dialog-notice__message").TextContent;
        Assert.StartsWith("The changes could not be saved.", question, StringComparison.Ordinal);
        Assert.Contains("changed or moved to another state by someone else", question, StringComparison.Ordinal);

        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Contains("someone else", dialog.Alerts(), StringComparison.Ordinal), Wait);
        Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog));
        Assert.True(context.HasChanges);
        Assert.Equal(0, circuit.Service.ChildQueries);
        Assert.Null((await LoadAsync(id, cancellationToken))!.Notes);

        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(TestParentDialogContext.ChildrenView, SelectedView(dialog)), Wait);
        Assert.False(context.HasChanges);
        Assert.Equal(TestParentState.Active, context.Entity!.State);
        dialog.Host.WaitForAssertion(() => Assert.Equal(1, circuit.Service.ChildQueries), Wait);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
        Assert.Empty(dialog.Alerts());
    }

    [Fact]
    public async Task A_hop_to_a_view_of_the_entitys_own_fields_keeps_the_edits_in_the_form()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var context = dialog.Context();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 0);

        await dialog.Host.InvokeAsync(() =>
        {
            context.Entity!.Notes = "Picked from a list";
            context.NotifyEdited();
        });
        dialog.WaitForEdit();

        SelectView(dialog, TestParentDialogContext.InfoView);
        dialog.Host.WaitForAssertion(() => Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog)), Wait);
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));
        Assert.True(context.HasChanges);
        Assert.Equal("Picked from a list", context.Entity!.Notes);
        Assert.NotNull(dialog.Host.Find("[role=tabpanel][data-view=info] [name=test-parent-notes]"));
        Assert.Null((await LoadAsync(id, cancellationToken))!.Notes);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_views_rows_load_only_when_it_is_first_selected_and_a_step_reads_them_again_only_while_it_is_shown()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Equal(0, circuit.Service.ChildQueries);
        Assert.Empty(dialog.Host.FindAll("[data-view=children] tr[data-row]"));

        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);
        Assert.Equal(1, circuit.Service.ChildQueries);

        SelectView(dialog, TestParentDialogContext.InfoView);
        dialog.Host.WaitForAssertion(() => Assert.True(dialog.Host.Find("[role=tabpanel][data-view=children]").HasAttribute("hidden")), Wait);
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.False(dialog.Host.Find("[role=tabpanel][data-view=children]").HasAttribute("hidden")), Wait);
        Assert.Equal(1, circuit.Service.ChildQueries);

        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal(2, circuit.Service.ChildQueries), Wait);

        SelectView(dialog, TestParentDialogContext.InfoView);
        dialog.Host.WaitForAssertion(() => Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog)), Wait);
        dialog.WaitUntilAsked(2);
        dialog.Step("test-parent-save").Click();
        dialog.WaitUntilAsked(3);
        await Task.Delay(200, cancellationToken);
        Assert.Equal(2, circuit.Service.ChildQueries);
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Equal(3, circuit.Service.ChildQueries), Wait);
        WaitForRows(dialog, 1);
    }

    [Fact]
    public async Task Leaving_a_view_cancels_its_load_and_showing_it_again_reads_the_rows_anew()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        circuit.Service.BeforeChildQuery = async token =>
        {
            held.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        await held.Task.WaitAsync(Wait, cancellationToken);
        var first = Assert.Single(circuit.Service.ChildQueryTokens);
        Assert.False(first.IsCancellationRequested);

        SelectView(dialog, TestParentDialogContext.InfoView);
        dialog.Host.WaitForAssertion(() => Assert.True(first.IsCancellationRequested), Wait);
        Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog));
        Assert.Empty(dialog.Alerts());

        circuit.Service.BeforeChildQuery = null;
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 1);
        Assert.Equal(2, circuit.Service.ChildQueries);
        Assert.False(circuit.Service.ChildQueryTokens[1].IsCancellationRequested);

        circuit.Service.BeforeChildQuery = token => Task.Delay(Timeout.Infinite, token);
        dialog.Step("test-parent-save").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(3, circuit.Service.ChildQueries), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Save: done.", dialog.Status()), Wait);
        await dialog.CloseByEscAsync();
        Assert.True(circuit.Service.ChildQueryTokens[2].IsCancellationRequested);
    }

    [Fact]
    public async Task Closing_a_child_that_changed_while_the_parent_has_edits_saves_them_before_the_parent_reloads()
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
            parent.Entity!.Notes = "Two groups this period";
            parent.NotifyEdited();
        });
        dialog.WaitForEdit();

        var child = await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);
        var loadsBefore = circuit.Probe.Loads;
        await CloseTopByEscAsync(dialog, layouts: 1);

        await WaitForNotesAsync(id, "Two groups this period", cancellationToken);
        Assert.Equal("test-parent-save", Assert.Single(await HistoryOfAsync(id, cancellationToken)).TransitionKey);
        dialog.Host.WaitForAssertion(() => Assert.False(parent.HasChanges), Wait);
        Assert.Equal("Two groups this period", parent.Entity!.Notes);
        Assert.True(circuit.Probe.Loads > loadsBefore);
        Assert.Empty(dialog.Host.FindAll(".form-dialog"));

        dialog.Host.WaitForAssertion(() => Assert.Equal("Active", dialog.Host.Find("[role=tabpanel][data-view=children] .list-state").TextContent), Wait);
        Assert.Equal(TestChildState.Active, (await LoadChildAsync(morning, cancellationToken))!.State);
        Assert.NotNull(child);
    }

    [Fact]
    public async Task Closing_a_child_that_changed_while_the_parent_has_edits_no_save_can_take_asks_before_the_parent_reloads()
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
            parent.Entity!.Name = string.Empty;
            parent.NotifyEdited();
        });
        dialog.WaitForEdit();
        var version = parent.Version;

        await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Active: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);
        await CloseTopByEscAsync(dialog, layouts: 1);

        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        Assert.Contains("Name is required", dialog.Host.Find(".form-dialog .dialog-notice__message").TextContent, StringComparison.Ordinal);
        Assert.Equal(version, parent.Version);
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal(version, parent.Version);
        Assert.True(parent.HasChanges);
        Assert.Equal(string.Empty, parent.Entity!.Name);
        Assert.Empty(await HistoryOfAsync(id, cancellationToken));

        dialog.Host.WaitForAssertion(() => Assert.Equal("Active", dialog.Host.Find("[role=tabpanel][data-view=children] .list-state").TextContent), Wait);

        await OpenRowAsync(circuit, dialog, morning);
        dialog.Step("test-child-cancel").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal("Canceled: done.", dialog.Host.FindAll(".state-entity-dialog-footer__status").Last().TextContent), Wait);
        await CloseTopByEscAsync(dialog, layouts: 1);
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        dialog.Host.WaitForAssertion(() => Assert.False(parent.HasChanges), Wait);
        Assert.True(parent.Version > version);
        Assert.Equal("Spring period job", parent.Entity!.Name);
    }

    [Fact]
    public async Task A_view_is_offered_only_with_its_permission_its_area_visible_and_a_saved_row()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var draft = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var canceled = await AddParentAsync(scenario.A, TestParentState.Canceled, "Summer job", cancellationToken);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, TestParentDialogService.ViewPermission);

        await using (var circuit = Circuit(scenario.A.Runtime()))
        {
            var dialog = await circuit.OpenLoadedAsync(draft);
            Assert.Equal([TestParentDialogContext.InfoView, TestParentDialogContext.ChildrenView], ViewKeys(dialog));
            await dialog.CloseByEscAsync();

            var hidden = await circuit.OpenLoadedAsync(canceled);
            Assert.Empty(hidden.Host.FindAll("[role=tablist]"));
            Assert.Empty(hidden.Host.FindAll("[role=tabpanel]"));
            Assert.NotNull(hidden.Host.Find("[name=test-parent-name]"));
            await hidden.CloseByEscAsync();

            var created = await circuit.OpenCreateAsync();
            Assert.Empty(created.Host.FindAll("[role=tablist]"));
            await created.CloseByEscAsync();
            Assert.Equal(0, circuit.Service.ChildQueries);
        }

        await using var viewers = Circuit(scenario.A.RuntimeOf(viewer));
        var refused = await viewers.OpenLoadedAsync(draft);
        Assert.Empty(refused.Host.FindAll("[role=tablist]"));
        Assert.NotNull(refused.Host.Find("[name=test-parent-name]"));
        Assert.Equal(0, viewers.Service.ChildQueries);
    }

    [Fact]
    public async Task The_child_list_keeps_its_query_out_of_the_address_inside_the_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Afternoon group", cancellationToken);
        await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 2);

        dialog.Host.Find("[role=tabpanel][data-view=children] input[type=search]").Change("Morning");
        WaitForRows(dialog, 1);
        Assert.Null(circuit.UrlState.ReadRaw("q"));
        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, id)], circuit.UrlState.DialogStack);
    }

    [Fact]
    public async Task A_session_loads_the_next_parent_before_it_swaps_it_in_so_no_loader_ever_shows()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var session = await OpenSessionAsync(circuit, [first, second]);
        Assert.Equal("Spring period job", session.Title());
        Assert.Equal("1 of 2", session.Host.Find(".state-entity-dialog-session__position").TextContent);

        var loaderShown = false;
        circuit.Dialog.OnMarkupUpdated += (_, _) => loaderShown |= circuit.Dialog.FindAll(".state-entity-dialog__body > [data-loading]").Count > 0;

        circuit.Probe.HoldLoads();
        session.Host.Find("[data-session-step=next]").Click();
        await circuit.Probe.LoadStarted.Task.WaitAsync(Wait, cancellationToken);
        Assert.Equal("Spring period job", session.Title());
        session.Host.WaitForAssertion(() => Assert.Equal("true", session.Host.Find(".state-entity-dialog-session").GetAttribute("aria-busy")), Wait);
        Assert.Empty(session.Host.FindAll(".state-entity-dialog__body > [data-loading]"));

        circuit.Probe.ReleaseLoads();
        session.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", session.Title()), Wait);
        Assert.Equal("Autumn period job", session.Host.Find("[name=test-parent-name]").GetAttribute("value"));
        Assert.Equal("2 of 2", session.Host.Find(".state-entity-dialog-session__position").TextContent);
        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, second)], circuit.UrlState.DialogStack);
        Assert.Single(session.Host.FindAll(".rz-dialog-wrapper"));

        session.Host.Find("[data-session-step=previous]").Click();
        session.Host.WaitForAssertion(() => Assert.Equal("Spring period job", session.Title()), Wait);
        Assert.False(loaderShown);
        Assert.Equal([new DialogStackSegment(TestParentDialogContext.Token, first)], circuit.UrlState.DialogStack);

        await session.CloseByEscAsync();
        Assert.Equal(new StateEntityDialogResult(false, false, first), await session.Result);
    }

    [Fact]
    public async Task Moving_on_in_a_session_settles_the_edits_first_saving_them_or_asking()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var second = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var session = await OpenSessionAsync(circuit, [first, second]);

        session.Host.Find("[name=test-parent-notes]").Change("Bring the projector");
        session.WaitForEdit();
        session.Host.Find("[data-session-step=next]").Click();
        session.Host.WaitForAssertion(() => Assert.Equal("Autumn period job", session.Title()), Wait);
        Assert.Equal("Bring the projector", (await LoadAsync(first, cancellationToken))!.Notes);

        session.Host.Find("[name=test-parent-name]").Change(string.Empty);
        session.WaitForEdit();
        session.Host.Find("[data-session-step=previous]").Click();
        session.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", session.Host.Find(".form-dialog-header__title").TextContent), Wait);
        session.ConfirmButton("Keep editing").Click();
        session.Host.WaitForAssertion(() => Assert.Empty(session.Host.FindAll(".form-dialog")), Wait);
        Assert.Equal("2 of 2", session.Host.Find(".state-entity-dialog-session__position").TextContent);
        Assert.Equal(string.Empty, session.Host.Find("[name=test-parent-name]").GetAttribute("value"));
        Assert.Equal("Autumn period job", (await LoadAsync(second, cancellationToken))!.Name);

        _ = session.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        session.Host.WaitForAssertion(() => Assert.Single(session.Host.FindAll(".form-dialog")), Wait);
        session.ConfirmButton("Discard changes").Click();
        Assert.True((await session.Result.WaitAsync(Wait, cancellationToken)).Changed);
    }

    private static List<string> ViewKeys(OpenDialog dialog) =>
        [.. dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelectorAll("[role=tab]").Select(tab => tab.GetAttribute("data-view")!)];

    private static string? SelectedView(OpenDialog dialog) =>
        dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelector("[role=tab][aria-selected=true]")?.GetAttribute("data-view");

    private static void SelectView(OpenDialog dialog, string key) =>
        RenderedClicks.ClickOnce(dialog.Host, () => dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelector($"[role=tab][data-view={key}]")!);

    private static void WaitForRows(OpenDialog dialog, int count) => dialog.Host.WaitForAssertion(() =>
    {
        var panel = dialog.Host.Find("[role=tabpanel][data-view=children]");
        Assert.Equal(count, panel.QuerySelectorAll("tr[data-row]").Length);
        Assert.NotNull(panel.QuerySelector(".list-view__summary"));
    }, Wait);

    private static List<string> RowNames(OpenDialog dialog) =>
        [.. dialog.Host.FindAll("[role=tabpanel][data-view=children] tr[data-row] .list-row__open").Select(button => button.TextContent.Trim())];

    private static async Task<StateEntityDialogContext> OpenRowAsync(DialogCircuit circuit, OpenDialog dialog, Guid row)
    {
        var before = dialog.Host.FindComponents<StateEntityDialogLayout>().Count;
        dialog.Host.Find($"[role=tabpanel][data-view=children] tr[data-row='{row}'] .list-row__open").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(before + 1, dialog.Host.FindComponents<StateEntityDialogLayout>().Count), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("loaded", dialog.Host.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        dialog.Host.WaitForAssertion(() => Assert.Equal("false", dialog.Host.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
        Assert.NotNull(circuit);
        return dialog.Host.FindComponents<StateEntityDialogLayout>().Last().Instance.Context;
    }

    private static async Task CloseTopByEscAsync(OpenDialog dialog, int layouts)
    {
        await dialog.Host.InvokeAsync(() => dialog.Host.Services.GetRequiredService<Radzen.DialogService>().TryCloseFromJs(null));
        dialog.Host.WaitForAssertion(() => Assert.Equal(layouts, dialog.Host.FindComponents<StateEntityDialogLayout>().Count), Wait);
    }

    private static async Task<OpenDialog> OpenSessionAsync(DialogCircuit circuit, IReadOnlyList<Guid> ids)
    {
        Task<StateEntityDialogResult>? result = null;
        await circuit.Dialog.InvokeAsync(() => { result = TestParentDialog.OpenSessionAsync(circuit.Dialogs, circuit.Service, ids, 0); });
        circuit.Dialog.WaitForAssertion(() => Assert.Equal("loaded", circuit.Dialog.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
        circuit.Dialog.WaitForAssertion(() => Assert.Equal("false", circuit.Dialog.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        return new OpenDialog(circuit.Dialog, result!);
    }

    private async Task<TestChild?> LoadChildAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestChildren.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
    }

    private async Task WaitForNotesAsync(Guid id, string notes, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + Wait;
        while ((await LoadAsync(id, cancellationToken))?.Notes != notes)
        {
            Assert.True(DateTime.UtcNow < until, $"The parent's notes never became \"{notes}\".");
            await Task.Delay(50, cancellationToken);
        }
    }
}
