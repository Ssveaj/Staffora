using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Blazor.Components.Common.List;
using Staffora.Blazor.Helper.UrlState;
using System.Text.RegularExpressions;

namespace Staffora.Test.Web.Lists;

public class ListViewTests : WebTestContext
{
    private static readonly ListTestRow ada = new(Guid.Parse("0a0a0a0a-0000-4000-8000-000000000001"), "Ada", "Alpha", IsActive: true);
    private static readonly ListTestRow bo = new(Guid.Parse("0b0b0b0b-0000-4000-8000-000000000002"), "Bo", "Beta", IsActive: false);
    private static readonly ListTestRow cy = new(Guid.Parse("0c0c0c0c-0000-4000-8000-000000000003"), "Cy", "Alpha", IsActive: true);
    private static readonly ListTestRow di = new(Guid.Parse("0d0d0d0d-0000-4000-8000-000000000004"), "Di", "Beta", IsActive: true);
    private static readonly ListTestRow ed = new(Guid.Parse("0e0e0e0e-0000-4000-8000-000000000005"), "Ed", "Beta", IsActive: true);

    private readonly UrlStateService urlState;

    public ListViewTests()
    {
        var module = JSInterop.SetupModule(JsUrlStateWriter.ModulePath);
        module.SetupVoid("apply", _ => true).SetVoidResult();
        this.urlState = Services.GetRequiredService<UrlStateService>();
    }

    [Fact]
    public void Rows_show_their_cells_and_status_and_the_strip_takes_the_rows_tone()
    {
        var source = new FakeRowSource([ListViewTests.ada, ListViewTests.bo, cy]);

        var list = Open(source);

        Assert.Equal(["Ada", "Cy"], Names(list));
        var ada = Row(list, ListViewTests.ada);
        Assert.Equal(["Ada", "Alpha", "Active"], ada.QuerySelectorAll("td[data-label]").Select(CellText));
        Assert.Contains("ui-tone--success", ada.ClassList);
        Assert.Equal("Status", list.FindAll("thead th").Last().TextContent.Trim());
        Assert.Equal("1–2 of 2 rows", list.Find("#rows-summary").TextContent.Trim());

        Assert.Contains("1 inactive row is hidden.", list.Find(".list-view__hidden").TextContent);
        list.Find(".list-view__hidden button").Click();
        list.WaitForAssertion(() => Assert.Equal(["Ada", "Bo"], Names(list)));
        var bo = Row(list, ListViewTests.bo);
        Assert.Contains("ui-tone--neutral", bo.ClassList);
        Assert.Equal("Inactive", bo.QuerySelector(".list-state")!.TextContent.Trim());
        Assert.NotNull(bo.QuerySelector(".list-state .list-state__dot[aria-hidden=true]"));
    }

    [Fact]
    public async Task A_click_on_a_row_opens_its_dialog_and_the_list_reads_its_page_again_when_it_closes()
    {
        var source = new FakeRowSource([ada, cy]);
        var opened = new List<ListTestRow>();
        var dialog = new TaskCompletionSource();
        var list = Open(source, onOpen: row =>
        {
            opened.Add(row);
            return dialog.Task;
        });
        var loadsBefore = source.Queries.Count;

        Row(list, cy).Click();

        Assert.Equal([cy], opened);

        source.Rows[1] = cy with { Name = "Cyrus" };
        Assert.Equal(loadsBefore, source.Queries.Count);
        await list.InvokeAsync(dialog.SetResult);

        list.WaitForAssertion(() => Assert.Equal(["Ada", "Cyrus"], Names(list)));
        Assert.Equal(loadsBefore + 1, source.Queries.Count);
    }

    [Fact]
    public async Task The_rows_name_is_a_button_that_opens_the_row_once_and_no_other_row_opens_while_its_dialog_is_open()
    {
        var source = new FakeRowSource([ada, cy]);
        var opened = new List<ListTestRow>();
        var dialog = new TaskCompletionSource();
        var list = Open(source, onOpen: row =>
        {
            opened.Add(row);
            return dialog.Task;
        });

        var name = Row(list, ada).QuerySelector("button.list-row__open")!;
        Assert.Equal("button", name.GetAttribute("type"));
        Assert.Equal("Ada", name.TextContent.Trim());
        name.Click();
        Assert.Equal([ada], opened);

        Row(list, cy).Click();
        Assert.Equal([ada], opened);

        Assert.Single(source.Queries);
        await list.InvokeAsync(dialog.SetResult);
        list.WaitForAssertion(() => Assert.Equal(2, source.Queries.Count));
        Assert.Equal([ada], opened);
    }

    [Fact]
    public void A_list_without_an_opener_has_no_open_buttons()
    {
        this.urlState.MarkInteractive();
        var list = Render<ListViewHost>(parameters => parameters.Add(host => host.Source, new FakeRowSource([ada])));
        list.WaitForAssertion(() => Assert.Single(Names(list)));

        Assert.Empty(list.FindAll("button.list-row__open"));
        Assert.Equal("Ada", CellText(Row(list, ada).QuerySelector("td[data-label='Name']")!));
    }

    [Fact]
    public void The_address_opens_the_list_with_its_search_filter_toggle_grouping_sort_and_page()
    {
        Navigate("/rows?q=%20alp%20&team=ALPHA&inactive=true&group=team&sort=-Name&page=1&utm=mail");
        var source = new FakeRowSource([ada, bo, cy, di]);

        var list = Open(source);

        var first = source.Queries[0];
        Assert.Equal("alp", first.Search);
        Assert.Equal("alpha", first.Filter(FakeRowSource.Team));
        Assert.True(first.IsOn(FakeRowSource.ShowInactive));
        Assert.Equal(("team", "name", true, 1), (first.Group, first.Sort, first.Descending, first.Page));
        Assert.Equal(["Cy", "Ada"], Names(list));

        Assert.Equal("alp", list.Find("#rows-search").GetAttribute("value"));
        Assert.Equal("alpha", list.Find("#rows-filter-team").GetAttribute("value"));
        Assert.Equal("team", list.Find("#rows-group").GetAttribute("value"));
        Assert.Equal("-name", list.Find("#rows-sort").GetAttribute("value"));
        Assert.Equal("true", list.Find(".list-toggle").GetAttribute("aria-pressed"));
        Assert.Empty(list.FindAll("thead th[aria-sort], thead th button"));
        Assert.Equal(("alp", "alpha", "-name", null, "mail"), (this.urlState.ReadRaw("q"), this.urlState.ReadRaw("team"), this.urlState.ReadRaw("sort"), this.urlState.ReadRaw("page"), this.urlState.ReadRaw("utm")));
    }

    [Fact]
    public void Values_the_list_does_not_offer_read_as_its_defaults_and_leave_the_address()
    {
        Navigate("/rows?team=gamma&inactive=yes&group=colour&sort=-shoe&page=0");
        var source = new FakeRowSource([ada, cy]);

        Open(source);

        Assert.Equal(FakeRowSource.Definition.Default, source.Queries[0]);
        Assert.All(["team", "inactive", "group", "sort", "page"], key => Assert.Null(this.urlState.ReadRaw(key)));
    }

    [Fact]
    public void A_filter_change_asks_for_the_first_page_and_writes_the_address()
    {
        var source = new FakeRowSource([ada, cy, di, ed]);
        var list = Open(source);
        list.Find(".list-pager__step:last-child").Click();
        list.WaitForAssertion(() => Assert.Equal(["Di", "Ed"], Names(list)));
        Assert.Equal("2", this.urlState.ReadRaw("page"));
        Assert.Equal("3–4 of 4 rows", list.Find("#rows-summary").TextContent.Trim());

        Choose(list, "#rows-filter-team", "beta");

        list.WaitForAssertion(() => Assert.Equal(["Di", "Ed"], Names(list)));
        var last = source.Queries[^1];
        Assert.Equal(("beta", 1), (last.Filter(FakeRowSource.Team), last.Page));
        Assert.Equal("beta", this.urlState.ReadRaw("team"));
        Assert.Null(this.urlState.ReadRaw("page"));

        Choose(list, "#rows-filter-team", string.Empty);
        list.WaitForAssertion(() => Assert.Null(source.Queries[^1].Filter(FakeRowSource.Team)));
        Assert.Null(this.urlState.ReadRaw("team"));
    }

    [Fact]
    public void The_search_and_the_sort_tool_ask_for_what_they_show_and_no_heading_sorts()
    {
        var source = new FakeRowSource([ada, cy, di]);
        var list = Open(source);

        list.Find("#rows-search").Change("  d  ");
        list.WaitForAssertion(() => Assert.Equal(["Ada", "Di"], Names(list)));
        Assert.Equal("d", source.Queries[^1].Search);
        Assert.Equal("d", this.urlState.ReadRaw("q"));

        Assert.Empty(list.FindAll("thead th button, thead th[aria-sort]"));
        Choose(list, "#rows-sort", "-team");
        list.WaitForAssertion(() => Assert.Equal(("team", true), (source.Queries[^1].Sort, source.Queries[^1].Descending)));
        Assert.Equal(["Di", "Ada"], Names(list));
        Assert.Equal("-team", this.urlState.ReadRaw("sort"));

        Choose(list, "#rows-sort", "name");
        list.WaitForAssertion(() => Assert.Equal(("name", false), (source.Queries[^1].Sort, source.Queries[^1].Descending)));
        Assert.Null(this.urlState.ReadRaw("sort"));
    }

    [Fact]
    public void Groups_head_their_rows_with_their_counts_and_collapse()
    {
        var source = new FakeRowSource([ada, bo, cy, di]);
        var list = Open(source);
        list.Find(".list-toggle").Click();
        list.WaitForAssertion(() => Assert.True(source.Queries[^1].IsOn(FakeRowSource.ShowInactive)));
        Assert.Equal("true", this.urlState.ReadRaw("inactive"));

        Choose(list, "#rows-group", "team");

        list.WaitForAssertion(() => Assert.Single(list.FindAll(".list-heading")));
        Assert.Equal("rowgroup", list.Find(".list-heading th").GetAttribute("scope"));
        Assert.Equal(("Team Alpha", "2 rows"), Heading(list));
        Assert.Equal(["Ada", "Cy"], Names(list));
        Assert.Equal("team", this.urlState.ReadRaw("group"));

        list.Find(".list-pager__step:last-child").Click();
        list.WaitForAssertion(() => Assert.Equal(["Bo", "Di"], Names(list)));
        Assert.Equal(("Team Beta", "2 rows"), Heading(list));

        var toggle = list.Find(".list-heading__toggle");
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        toggle.Click();
        Assert.Equal("false", list.Find(".list-heading__toggle").GetAttribute("aria-expanded"));
        Assert.Empty(Names(list));
        list.Find(".list-heading__toggle").Click();
        Assert.Equal(["Bo", "Di"], Names(list));
    }

    [Fact]
    public void A_page_past_the_last_is_shown_as_the_last_page_and_the_address_follows()
    {
        Navigate("/rows?page=9");
        var source = new FakeRowSource([ada, cy, di]);

        var list = Open(source);

        Assert.Equal(9, source.Queries[0].Page);
        Assert.Equal(["Di"], Names(list));
        Assert.Equal("2", list.Find(".list-pager__page[aria-current=page]").TextContent.Trim());
        Assert.Equal("2", this.urlState.ReadRaw("page"));
    }

    [Fact]
    public void An_empty_list_and_a_list_without_matches_say_which()
    {
        var empty = Open(new FakeRowSource([]));
        Assert.Equal("No rows yet", empty.Find(".empty-state__title").TextContent.Trim());
        Assert.Empty(empty.FindAll("table"));

        Navigate("/rows?q=zz&team=beta");
        var source = new FakeRowSource([ada]);
        var list = Open(source);
        Assert.Equal("No rows match", list.Find(".empty-state__title").TextContent.Trim());

        list.Find(".empty-state button").Click();

        list.WaitForAssertion(() => Assert.Equal(["Ada"], Names(list)));
        Assert.Equal(("", null), (source.Queries[^1].Search, source.Queries[^1].Filter(FakeRowSource.Team)));
        Assert.Null(this.urlState.ReadRaw("q"));
    }

    [Fact]
    public void A_refused_list_shows_the_services_reason()
    {
        var list = Open(new FakeRowSource([ada]) { Failure = "You do not have permission to view rows." });

        Assert.Equal("alert", list.Find(".empty-state").GetAttribute("role"));
        Assert.Contains("You do not have permission to view rows.", list.Find(".empty-state").TextContent);
        Assert.Empty(list.FindAll("table"));
    }

    [Fact]
    public async Task A_slower_older_load_never_replaces_a_newer_one()
    {
        var first = new TaskCompletionSource();
        var source = new FakeRowSource([ada, cy]) { Hold = query => query.Search.Length == 0 ? first.Task : Task.CompletedTask };
        var list = Render<ListViewHost>(parameters => parameters.Add(host => host.Source, source));
        Assert.Contains("Loading rows…", list.Find("[role=status]").TextContent);

        list.Find("#rows-search").Change("cy");
        list.WaitForAssertion(() => Assert.Equal(["Cy"], Names(list)));
        await list.InvokeAsync(first.SetResult);

        Assert.Equal(["Cy"], Names(list));
    }

    [Fact]
    public async Task A_link_to_the_list_with_other_values_wins_while_it_is_on_screen()
    {
        this.urlState.EnterPage(typeof(ListViewHost));
        var source = new FakeRowSource([ada, cy, di]);
        var list = Open(source);

        var navigation = Services.GetRequiredService<NavigationManager>();
        await list.InvokeAsync(() =>
        {
            navigation.NavigateTo("/rows?team=beta");
            this.urlState.EnterPage(typeof(ListViewHost));
        });

        list.WaitForAssertion(() => Assert.Equal(["Di"], Names(list)));
        Assert.Equal("beta", source.Queries[^1].Filter(FakeRowSource.Team));
        Assert.Equal("beta", list.Find("#rows-filter-team").GetAttribute("value"));
    }

    [Fact]
    public void A_list_that_does_not_bind_the_address_leaves_it_alone()
    {
        Navigate("/rows?team=beta");
        var source = new FakeRowSource([ada, di]);
        var list = Open(source, bindAddress: false);

        Assert.Null(source.Queries[0].Filter(FakeRowSource.Team));
        Choose(list, "#rows-filter-team", "alpha");
        list.WaitForAssertion(() => Assert.Equal(["Ada"], Names(list)));
        Assert.Equal("beta", this.urlState.ReadRaw("team"));
    }

    [Fact]
    public async Task When_a_rows_dialog_closes_the_focus_goes_back_to_that_rows_name()
    {
        var module = JSInterop.SetupModule(ListView<ListTestRow>.ModulePath);
        module.SetupVoid("focusRow", _ => true).SetVoidResult();
        var dialog = new TaskCompletionSource();
        var list = Open(new FakeRowSource([ada, cy]), onOpen: _ => dialog.Task);

        Row(list, cy).QuerySelector("button.list-row__open")!.Click();

        module.VerifyNotInvoke("focusRow");
        await list.InvokeAsync(dialog.SetResult);

        list.WaitForAssertion(() => module.VerifyInvoke("focusRow"));
        var invocation = module.VerifyInvoke("focusRow");
        Assert.Equal(cy.Id.ToString(), invocation.Arguments[1]);
        Assert.IsType<ElementReference>(invocation.Arguments[0]);
    }

    [Fact]
    public void Two_lists_on_a_page_keep_their_queries_apart_under_their_own_address_prefixes()
    {
        Navigate("/rows?q=ad&second.team=beta");
        var first = new FakeRowSource([ada, bo, cy, di]);
        var second = new FakeRowSource([ada, bo, cy, di]);
        this.urlState.MarkInteractive();

        var page = Render<TwoListsHost>(parameters => parameters
            .Add(host => host.First, first)
            .Add(host => host.Second, second)
            .Add(host => host.SecondPrefix, "second"));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".list-view__loading")));
        Assert.Equal(("ad", null), (first.Queries[0].Search, first.Queries[0].Filter(FakeRowSource.Team)));
        Assert.Equal(("", "beta"), (second.Queries[0].Search, second.Queries[0].Filter(FakeRowSource.Team)));

        var ids = page.FindAll(".list-view").Select(element => element.Id).ToList();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal("second-list", ids[1]);
        Assert.StartsWith("list-", ids[0]);

        Choose(page, "#second-list-filter-team", "alpha");
        Choose(page, $"#{ids[0]}-filter-team", "beta");

        page.WaitForAssertion(() => Assert.Equal("alpha", second.Queries[^1].Filter(FakeRowSource.Team)));
        Assert.Equal(("beta", "alpha", "ad"), (this.urlState.ReadRaw("team"), this.urlState.ReadRaw("second.team"), this.urlState.ReadRaw("q")));
        Assert.Null(this.urlState.ReadRaw("second.q"));
    }

    [Fact]
    public void Two_lists_that_would_share_the_address_fail_as_the_second_binds()
    {
        this.urlState.MarkInteractive();

        var refused = Assert.ThrowsAny<Exception>(() => Render<TwoListsHost>(parameters => parameters
            .Add(host => host.First, new FakeRowSource([ada]))
            .Add(host => host.Second, new FakeRowSource([ada]))));
        var reason = Assert.IsType<InvalidOperationException>(refused is InvalidOperationException ? refused : refused.GetBaseException());
        Assert.Contains("AddressPrefix", reason.Message);

        Assert.ThrowsAny<Exception>(() => Render<TwoListsHost>(parameters => parameters
            .Add(host => host.First, new FakeRowSource([ada]))
            .Add(host => host.Second, new FakeRowSource([ada]))
            .Add(host => host.FirstPrefix, "same")
            .Add(host => host.SecondPrefix, "same")));
    }

    [Fact]
    public void A_list_inside_a_dialog_leaves_the_address_alone_unless_told_to_bind_it()
    {
        Navigate("/rows?team=beta");
        this.urlState.MarkInteractive();
        var source = new FakeRowSource([ada, di]);

        var dialog = Render<DialogListHost>(parameters => parameters.Add(host => host.Source, source));
        dialog.WaitForAssertion(() => Assert.Empty(dialog.FindAll(".list-view__loading")));
        Assert.False(dialog.FindComponent<ListView<ListTestRow>>().Instance.IsBoundToAddress);
        Assert.Null(source.Queries[0].Filter(FakeRowSource.Team));
        Choose(dialog, "#child-filter-team", "alpha");
        dialog.WaitForAssertion(() => Assert.Equal("alpha", source.Queries[^1].Filter(FakeRowSource.Team)));
        Assert.Equal("beta", this.urlState.ReadRaw("team"));
        dialog.Dispose();

        var bound = new FakeRowSource([ada, di]);
        var told = Render<DialogListHost>(parameters => parameters.Add(host => host.Source, bound).Add(host => host.BindAddress, true));
        told.WaitForAssertion(() => Assert.Empty(told.FindAll(".list-view__loading")));
        Assert.Equal("beta", bound.Queries[0].Filter(FakeRowSource.Team));
    }

    [Fact]
    public void The_toolbars_choices_fold_behind_a_filters_button_that_counts_the_choices_made()
    {
        var list = Open(new FakeRowSource([ada, bo, cy]));
        var button = list.Find("button.list-filters-toggle");
        Assert.Equal(("false", "rows-filters", "Filters"), (button.GetAttribute("aria-expanded"), button.GetAttribute("aria-controls"), button.QuerySelector(".list-filters-toggle__text")!.TextContent.Trim()));
        var region = list.Find("#rows-filters");
        Assert.DoesNotContain("list-view__filters--open", region.ClassList);

        Assert.NotNull(region.QuerySelector("#rows-filter-team"));
        Assert.NotNull(region.QuerySelector("#rows-group"));
        Assert.NotNull(region.QuerySelector("#rows-sort"));
        Assert.NotNull(region.QuerySelector(".list-toggle"));
        Assert.Null(region.QuerySelector("#rows-search"));

        Choose(list, "#rows-filter-team", "alpha");
        list.Find(".list-toggle").Click();
        list.Find("#rows-search").Change("a");
        list.WaitForAssertion(() => Assert.Equal("Filters (2)", list.Find(".list-filters-toggle__text").TextContent.Trim()));
        Choose(list, "#rows-sort", "-name");
        list.WaitForAssertion(() => Assert.Equal("Filters (3)", list.Find(".list-filters-toggle__text").TextContent.Trim()));

        list.Find("button.list-filters-toggle").Click();
        Assert.Equal("true", list.Find("button.list-filters-toggle").GetAttribute("aria-expanded"));
        Assert.Contains("list-view__filters--open", list.Find("#rows-filters").ClassList);
        list.Find("button.list-filters-toggle").Click();
        Assert.Equal("false", list.Find("button.list-filters-toggle").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void The_table_names_its_roles_and_a_cards_label_is_hidden_from_assistive_technology()
    {
        var list = Open(new FakeRowSource([ada, cy]));

        Assert.Equal("table", list.Find("table.list-table").GetAttribute("role"));
        Assert.All(list.FindAll("thead, tbody"), group => Assert.Equal("rowgroup", group.GetAttribute("role")));
        Assert.All(list.FindAll("tr"), row => Assert.Equal("row", row.GetAttribute("role")));
        Assert.All(list.FindAll("thead th"), heading => Assert.Equal("columnheader", heading.GetAttribute("role")));
        Assert.All(list.FindAll("tr.list-row td"), cell => Assert.Equal("cell", cell.GetAttribute("role")));

        var labels = Row(list, ada).QuerySelectorAll(".list-cell__label");
        Assert.Equal(["Name", "Team", "Status"], labels.Select(label => label.TextContent.Trim()));
        Assert.All(labels, label => Assert.Equal("true", label.GetAttribute("aria-hidden")));

        Assert.Equal(["Name", "Team"], list.FindAll("thead th").Take(2).Select(heading => heading.TextContent.Trim()));
    }

    [Fact]
    public void A_set_filters_chip_shows_its_label_and_value_and_a_clear_button_that_sets_it_back_to_all()
    {
        Navigate("/rows?team=beta");
        var list = Open(new FakeRowSource([ada, di]));

        var chip = list.Find("#rows-filter-team");
        Assert.Equal("Team: Beta", Regex.Replace(chip.TextContent, @"\s+", " ").Replace("expand_more", string.Empty).Trim());
        Assert.Equal("beta", chip.GetAttribute("value"));
        Assert.Contains("list-chip--on", chip.ParentElement!.ClassList);
        var clear = list.Find("#rows-filter-team ~ button.list-chip__clear");
        Assert.Equal("Clear Team", clear.GetAttribute("aria-label"));

        clear.Click();
        list.WaitForAssertion(() => Assert.Equal(["Ada", "Di"], Names(list)));
        Assert.Null(this.urlState.ReadRaw("team"));
        Assert.Equal("Team: All", Regex.Replace(list.Find("#rows-filter-team").TextContent, @"\s+", " ").Replace("expand_more", string.Empty).Trim());
        Assert.DoesNotContain("list-chip--on", list.Find("#rows-filter-team").ParentElement!.ClassList);
        Assert.Empty(list.FindAll("button.list-chip__clear"));
    }

    [Fact]
    public void A_chip_opens_a_listbox_of_its_choices_and_a_choice_closes_it_and_gives_the_focus_back_to_the_chip()
    {
        var module = JSInterop.SetupModule(ListView<ListTestRow>.ModulePath);
        module.SetupVoid("attachPopovers", _ => true).SetVoidResult();
        module.SetupVoid("openPopover", _ => true).SetVoidResult();
        module.SetupVoid("focusControl", _ => true).SetVoidResult();
        var list = Open(new FakeRowSource([ada, di]));
        var chip = list.Find("#rows-filter-team");
        Assert.Equal(("listbox", "false", null), (chip.GetAttribute("aria-haspopup"), chip.GetAttribute("aria-expanded"), chip.GetAttribute("aria-controls")));
        Assert.Empty(list.FindAll("[role=listbox]"));

        chip.Click();

        var listbox = list.Find("[role=listbox]");
        Assert.Equal(("true", listbox.Id), (list.Find("#rows-filter-team").GetAttribute("aria-expanded"), list.Find("#rows-filter-team").GetAttribute("aria-controls")));
        Assert.Equal("Team", listbox.GetAttribute("aria-label"));
        Assert.Equal(["", "alpha", "beta"], listbox.QuerySelectorAll("[role=option]").Select(option => option.GetAttribute("data-value")));
        Assert.Equal(["true", "false", "false"], listbox.QuerySelectorAll("[role=option]").Select(option => option.GetAttribute("aria-selected")));
        list.WaitForAssertion(() => Assert.Equal(listbox.Id, module.VerifyInvoke("openPopover").Arguments[1]));

        list.Find("[role=option][data-value='alpha']").Click();
        list.WaitForAssertion(() => Assert.Equal(["Ada"], Names(list)));
        Assert.Empty(list.FindAll("[role=listbox]"));
        Assert.Equal("false", list.Find("#rows-filter-team").GetAttribute("aria-expanded"));
        list.WaitForAssertion(() => Assert.Equal("rows-filter-team", module.VerifyInvoke("focusControl").Arguments[1]));

        list.Find("#rows-sort").Click();
        Assert.Single(list.FindAll("[role=listbox]"));
        list.InvokeAsync(() => list.FindComponent<ListView<ListTestRow>>().Instance.ClosePopover());
        list.WaitForAssertion(() => Assert.Empty(list.FindAll("[role=listbox]")));
    }

    [Fact]
    public void A_quick_filter_shows_its_options_as_segments_the_chosen_one_with_the_count()
    {
        var totals = new List<int>();
        this.urlState.MarkInteractive();
        var list = Render<ListViewHost>(parameters => parameters
            .Add(host => host.Source, new FakeRowSource([ada, cy, di]))
            .Add(host => host.QuickFilter, FakeRowSource.Team)
            .Add(host => host.OnTotal, total => totals.Add(total)));
        list.WaitForAssertion(() => Assert.Empty(list.FindAll(".list-view__loading")));

        var group = list.Find(".list-quick");
        Assert.Equal(("group", "Team"), (group.GetAttribute("role"), group.GetAttribute("aria-label")));
        Assert.Null(list.Find("#rows-filters").QuerySelector(".list-quick"));
        Assert.Empty(list.FindAll("#rows-filter-team"));
        Assert.Equal(["true", "false", "false"], group.QuerySelectorAll("button").Select(button => button.GetAttribute("aria-pressed")));
        Assert.Equal("3", group.QuerySelector("[aria-pressed=true] .list-quick__count")!.TextContent.Trim());
        Assert.Equal(3, totals[^1]);

        list.Find(".list-quick button[data-value='beta']").Click();
        list.WaitForAssertion(() => Assert.Equal(["Di"], Names(list)));
        Assert.Equal("beta", this.urlState.ReadRaw("team"));
        Assert.Equal("1", list.Find(".list-quick [aria-pressed=true] .list-quick__count").TextContent.Trim());
        Assert.Equal(1, totals[^1]);
        Assert.Equal("Filters", list.Find(".list-filters-toggle__text").TextContent.Trim());
    }

    [Fact]
    public async Task Rows_as_cards_open_by_their_title_and_hand_the_focus_back_as_the_table_does()
    {
        var module = JSInterop.SetupModule(ListView<ListTestRow>.ModulePath);
        module.SetupVoid("focusRow", _ => true).SetVoidResult();
        var dialog = new TaskCompletionSource();
        var opened = new List<ListTestRow>();
        this.urlState.MarkInteractive();
        var list = Render<ListViewHost>(parameters => parameters
            .Add(host => host.Source, new FakeRowSource([ada, bo, cy]))
            .Add(host => host.AsCards, true)
            .Add(host => host.OnOpen, row => { opened.Add(row); return dialog.Task; }));
        list.WaitForAssertion(() => Assert.Empty(list.FindAll(".list-view__loading")));

        Assert.Empty(list.FindAll("table"));
        var card = list.Find($"li.list-row.list-card[data-row='{cy.Id}']");
        Assert.Equal(["Mon", "1", "Feb"], card.QuerySelectorAll(".list-card__date > span").Select(part => part.TextContent.Trim()));
        Assert.Equal("Cy", card.QuerySelector("button.list-row__open")!.TextContent.Trim());
        Assert.Contains("Alpha", card.QuerySelector(".list-card__facts")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("Open to see the row", card.QuerySelector(".list-card__hint")!.TextContent.Trim());
        Assert.Equal("Active", card.QuerySelector(".list-state")!.TextContent.Trim());
        Assert.Contains("--list-card-edge: #2196F3", card.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Empty(card.QuerySelectorAll("[data-badge]"));

        card.QuerySelector("button.list-row__open")!.Click();
        Assert.Equal([cy], opened);
        module.VerifyNotInvoke("focusRow");
        await list.InvokeAsync(dialog.SetResult);
        list.WaitForAssertion(() => Assert.Equal(cy.Id.ToString(), module.VerifyInvoke("focusRow").Arguments[1]));
    }

    [Fact]
    public void The_pager_offers_the_ends_the_current_page_and_its_neighbours()
    {
        Assert.Equal([1, 2], ListView<ListTestRow>.PagerItems(1, 2));
        Assert.Equal([1, 2, 3, null, 9], ListView<ListTestRow>.PagerItems(1, 9));
        Assert.Equal([1, null, 4, 5, 6, null, 9], ListView<ListTestRow>.PagerItems(5, 9));
        Assert.Equal([1, null, 7, 8, 9], ListView<ListTestRow>.PagerItems(9, 9));
        Assert.Equal([1, 2, 3, 4, 5], ListView<ListTestRow>.PagerItems(3, 5));
        Assert.Equal("ST", ListView<ListTestRow>.Initials("Sam Taylor"));
        Assert.Equal("RL", ListView<ListTestRow>.Initials("robin.lee@company.example"));
    }

    private static void Choose<TComponent>(IRenderedComponent<TComponent> rendered, string trigger, string value) where TComponent : IComponent
    {
        rendered.Find(trigger).Click();
        rendered.Find($"{trigger} ~ [role=listbox] [role=option][data-value='{value}']").Click();
    }

    private IRenderedComponent<ListViewHost> Open(FakeRowSource source, Func<ListTestRow, Task>? onOpen = null, bool bindAddress = true)
    {
        this.urlState.MarkInteractive();
        var list = Render<ListViewHost>(parameters => parameters
            .Add(host => host.Source, source)
            .Add(host => host.OnOpen, onOpen ?? (_ => Task.CompletedTask))
            .Add(host => host.BindAddress, bindAddress));
        list.WaitForAssertion(() => Assert.Empty(list.FindAll(".list-view__loading")));
        return list;
    }

    private void Navigate(string address) => Services.GetRequiredService<NavigationManager>().NavigateTo(address);

    private static List<string> Names(IRenderedComponent<ListViewHost> list) =>
        [.. list.FindAll("tr.list-row td[data-label='Name']").Select(CellText)];

    private static string CellText(IElement cell) =>
        string.Concat(cell.ChildNodes.Where(node => node is not IElement element || !element.ClassList.Contains("list-cell__label")).Select(node => node.TextContent)).Trim();

    private static IElement Row(IRenderedComponent<ListViewHost> list, ListTestRow row) => list.Find($"tr.list-row[data-row='{row.Id}']");

    private static (string Label, string Count) Heading(IRenderedComponent<ListViewHost> list) =>
        (list.Find(".list-heading__label").TextContent.Trim(), list.Find(".list-heading__count").TextContent.Trim());
}
