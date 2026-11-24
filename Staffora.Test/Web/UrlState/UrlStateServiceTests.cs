using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Test.Web.UrlState;

public class UrlStateServiceTests
{
    public enum StatusFilter { All, Active, Inactive }

    private static readonly UrlStateKey<string> searchKey = UrlStateKey.Text(UrlStateKeys.Search);
    private static readonly UrlStateKey<StatusFilter> statusKey = UrlStateKey.Enum(UrlStateKeys.Status, StatusFilter.All);
    private static readonly UrlStateKey<int> daysKey = UrlStateKey.Int(UrlStateKeys.Days, 7, value => value is 1 or 7 or 30 or 90);
    private static readonly UrlStateKey<Guid?> unitKey = UrlStateKey.Guid("unit");
    private static readonly UrlStateKey<bool> archivedKey = UrlStateKey.Bool("archived");

    private sealed class ListPage;
    private sealed class TenantPage;

    [Fact]
    public async Task Filter_keys_round_trip_through_the_address_and_survive_a_reload()
    {
        var browser = new TestBrowser("/tenant/management/users");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var unit = Guid.NewGuid();

        var search = service.Bind(searchKey);
        var status = service.Bind(statusKey);
        var days = service.Bind(daysKey);
        var unitFilter = service.Bind(unitKey);
        var archived = service.Bind(archivedKey);
        search.Value = "ann lee";
        status.Value = StatusFilter.Inactive;
        days.Value = 30;
        unitFilter.Value = unit;
        archived.Value = true;
        await service.WhenWrittenAsync();

        Assert.Equal(("ann lee", "inactive", "30", unit.ToString("D"), "true"), (browser.Query("q"), browser.Query("status"), browser.Query("days"), browser.Query("unit"), browser.Query("archived")));

        var reloaded = new TestBrowser(browser.PathAndQuery);
        using var afterReload = reloaded.CreateService();
        Assert.Equal(("ann lee", StatusFilter.Inactive, 30, (Guid?)unit, true), (afterReload.Bind(searchKey).Value, afterReload.Bind(statusKey).Value, afterReload.Bind(daysKey).Value, afterReload.Bind(unitKey).Value, afterReload.Bind(archivedKey).Value));
    }

    [Fact]
    public async Task A_value_set_back_to_its_default_leaves_the_address()
    {
        var browser = new TestBrowser("/superadmin/errorlog?status=active&days=30");
        using var service = browser.CreateService();
        service.MarkInteractive();

        service.Bind(statusKey).Value = StatusFilter.All;
        service.Bind(daysKey).Value = 7;
        service.Bind(searchKey).Value = "   ";
        await service.WhenWrittenAsync();

        Assert.Equal("/superadmin/errorlog", browser.PathAndQuery);
    }

    [Fact]
    public async Task Malformed_values_read_as_defaults_and_are_cleaned_from_the_address_on_bind()
    {
        var browser = new TestBrowser($"/list?status=deleted&days=31&unit=not-an-id&archived=maybe&q={new string('x', 600)}");
        using var service = browser.CreateService();
        service.MarkInteractive();

        var values = (service.Bind(statusKey).Value, service.Bind(daysKey).Value, service.Bind(unitKey).Value, service.Bind(archivedKey).Value, service.Bind(searchKey).Value);
        await service.WhenWrittenAsync();

        Assert.Equal((StatusFilter.All, 7, (Guid?)null, false, string.Empty), values);
        Assert.Equal("/list", browser.PathAndQuery);
    }

    [Fact]
    public async Task Another_spelling_of_a_value_is_rewritten_to_the_keys_own()
    {
        var browser = new TestBrowser("/list?status=INACTIVE");
        using var service = browser.CreateService();
        service.MarkInteractive();

        Assert.Equal(StatusFilter.Inactive, service.Bind(statusKey).Value);
        await service.WhenWrittenAsync();

        Assert.Equal("inactive", browser.Query("status"));
    }

    [Fact]
    public async Task Keys_the_page_does_not_own_are_kept_and_each_write_names_only_what_changed()
    {
        var browser = new TestBrowser("/list?utm=mail&q=old&other=1");
        using var service = browser.CreateService();
        service.MarkInteractive();

        var search = service.Bind(searchKey);
        Assert.Equal("old", search.Value);
        search.Value = "new";
        await service.WhenWrittenAsync();

        Assert.Equal([new Dictionary<string, string?> { ["q"] = "new" }], browser.Writer.Writes.Select(write => write.Changes));
        Assert.Equal(("mail", "new", "1"), (browser.Query("utm"), browser.Query("q"), browser.Query("other")));
    }

    [Fact]
    public async Task Nothing_is_written_until_the_circuit_is_interactive_and_then_the_latest_values_go()
    {
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();

        var status = service.Bind(statusKey);
        status.Value = StatusFilter.Active;
        status.Value = StatusFilter.Inactive;
        await service.WhenWrittenAsync();
        Assert.Empty(browser.Writer.Writes);
        Assert.False(service.IsInteractive);

        service.MarkInteractive();
        await service.WhenWrittenAsync();

        Assert.True(service.IsInteractive);
        Assert.Equal("inactive", browser.Query("status"));
        Assert.Single(browser.Writer.Writes);
    }

    [Fact]
    public async Task A_page_left_behind_neither_writes_nor_removes_its_keys()
    {
        var browser = new TestBrowser("/users?q=ann");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var usersSearch = service.Bind(searchKey);

        browser.Navigate("/superadmin/errorlog?q=boom");
        var logSearch = service.Bind(searchKey);
        usersSearch.Value = "late";
        usersSearch.Dispose();
        await service.WhenWrittenAsync();

        Assert.Equal("boom", logSearch.Value);
        Assert.Equal("boom", browser.Query("q"));
        Assert.All(browser.Writer.Writes, write => Assert.Equal("/superadmin/errorlog", write.Path));
    }

    [Fact]
    public async Task A_page_left_behind_cannot_take_a_key_from_the_next_page_before_that_page_binds_it()
    {
        var browser = new TestBrowser("/users");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var usersStatus = service.Bind(statusKey);

        browser.Navigate("/tenant/management/roles?status=active");
        usersStatus.Value = StatusFilter.Inactive;
        usersStatus.Dispose();
        await service.WhenWrittenAsync();
        var rolesStatus = service.Bind(statusKey);

        Assert.Equal(StatusFilter.Active, rolesStatus.Value);
        Assert.Equal("/tenant/management/roles?status=active", browser.PathAndQuery);
        Assert.Empty(browser.Writer.Writes);
    }

    [Fact]
    public async Task A_page_that_binds_before_the_navigation_event_reads_the_new_address()
    {
        var browser = new TestBrowser("/users?q=ann");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var usersSearch = service.Bind(searchKey);

        browser.Navigate("/superadmin/errorlog?q=boom", raiseEvent: false);
        var logSearch = service.Bind(searchKey);
        usersSearch.Dispose();
        browser.RaiseLocationChanged();
        logSearch.Value = "crash";
        await service.WhenWrittenAsync();

        Assert.Equal("crash", browser.Query("q"));
        Assert.Equal("/superadmin/errorlog?q=crash", browser.PathAndQuery);
    }

    [Fact]
    public async Task A_component_that_goes_while_its_page_stays_takes_its_key_with_it()
    {
        var browser = new TestBrowser("/list?status=active&q=x");
        using var service = browser.CreateService();
        service.MarkInteractive();

        service.Bind(searchKey);
        service.Bind(statusKey).Dispose();
        await service.WhenWrittenAsync();

        Assert.Equal("/list?q=x", browser.PathAndQuery);
    }

    [Fact]
    public async Task A_navigation_the_router_keeps_the_page_through_that_names_the_same_values_sets_the_address_to_the_pages_spelling()
    {
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();
        service.MarkInteractive();
        service.EnterPage(typeof(ListPage));
        var status = service.Bind(statusKey);
        status.Value = StatusFilter.Inactive;
        await service.WhenWrittenAsync();
        var changes = 0;
        status.Changed += () => changes++;

        browser.Navigate("/list?status=INACTIVE");
        service.EnterPage(typeof(ListPage));
        await service.WhenWrittenAsync();

        Assert.Equal((StatusFilter.Inactive, 0), (status.Value, changes));
        Assert.Equal("inactive", browser.Query("status"));
    }

    [Theory]
    [InlineData("/list?status=active", StatusFilter.Active, "active")]
    [InlineData("/list", StatusFilter.All, null)]
    public async Task A_navigation_the_router_keeps_the_page_through_that_names_other_values_wins_and_tells_the_page(string link, StatusFilter expected, string? expectedInAddress)
    {
        var browser = new TestBrowser("/list?status=inactive&q=ann");
        using var service = browser.CreateService();
        service.MarkInteractive();
        service.EnterPage(typeof(ListPage));
        var status = service.Bind(statusKey);
        var search = service.Bind(searchKey);
        var changed = new List<string>();
        status.Changed += () => changed.Add("status");
        search.Changed += () => changed.Add("q");

        browser.Navigate(link);
        service.EnterPage(typeof(ListPage));
        await service.WhenWrittenAsync();

        Assert.Equal((expected, string.Empty), (status.Value, search.Value));
        Assert.Equal(["status", "q"], changed);
        Assert.Equal((expectedInAddress, (string?)null), (browser.Query("status"), browser.Query("q")));

        status.Value = StatusFilter.Inactive;
        await service.WhenWrittenAsync();
        Assert.Equal("inactive", browser.Query("status"));
    }

    [Fact]
    public async Task A_page_the_router_keeps_when_only_its_route_parameter_changes_still_writes()
    {
        var browser = new TestBrowser("/tenants/a");
        using var service = browser.CreateService();
        service.MarkInteractive();
        service.EnterPage(typeof(TenantPage));
        var status = service.Bind(statusKey);

        browser.Navigate("/tenants/b");
        service.EnterPage(typeof(TenantPage));
        status.Value = StatusFilter.Active;
        await service.WhenWrittenAsync();

        Assert.Equal("active", browser.Query("status"));
        Assert.Equal("/tenants/b", browser.Writer.Writes[^1].Path);
    }

    [Fact]
    public async Task A_page_the_router_keeps_when_only_the_case_of_its_path_changes_still_writes_and_removes()
    {
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();
        service.MarkInteractive();
        service.EnterPage(typeof(ListPage));
        var status = service.Bind(statusKey);
        var search = service.Bind(searchKey);

        browser.Navigate("/List");
        service.EnterPage(typeof(ListPage));
        status.Value = StatusFilter.Active;
        search.Value = "ann";
        await service.WhenWrittenAsync();
        Assert.Equal(("active", "ann"), (browser.Query("status"), browser.Query("q")));

        search.Dispose();
        await service.WhenWrittenAsync();
        Assert.Equal("/List?status=active", browser.PathAndQuery);
    }

    [Fact]
    public async Task A_page_of_another_type_on_the_same_path_ends_the_old_pages_ownership()
    {
        var browser = new TestBrowser("/list?status=active");
        using var service = browser.CreateService();
        service.MarkInteractive();
        service.EnterPage(typeof(ListPage));
        var old = service.Bind(statusKey);

        browser.Navigate("/list?status=inactive");
        service.EnterPage(typeof(TenantPage));
        old.Value = StatusFilter.All;
        old.Dispose();
        await service.WhenWrittenAsync();

        Assert.Equal("inactive", browser.Query("status"));
        Assert.Empty(browser.Writer.Writes);
    }

    [Fact]
    public async Task Until_the_router_says_whether_it_kept_the_page_a_navigation_suspends_its_bindings()
    {
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();
        service.MarkInteractive();
        service.EnterPage(typeof(ListPage));
        var status = service.Bind(statusKey);

        browser.Navigate("/other?status=inactive");
        status.Value = StatusFilter.Active;
        await service.WhenWrittenAsync();

        Assert.Equal("inactive", browser.Query("status"));
        Assert.Empty(browser.Writer.Writes);
    }

    [Fact]
    public void A_second_binding_of_a_name_on_one_page_supersedes_the_first()
    {
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();

        var first = service.Bind(statusKey);
        var second = service.Bind(statusKey);
        first.Value = StatusFilter.Active;
        second.Value = StatusFilter.Inactive;
        first.Value = StatusFilter.All;

        Assert.Equal("inactive", service.ReadRaw(UrlStateKeys.Status));
    }

    [Fact]
    public void The_dialog_stack_key_is_reserved()
    {
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();

        Assert.Throws<InvalidOperationException>(() => service.Bind(UrlStateKey.Text(UrlStateKeys.DialogStack)));
    }

    [Fact]
    public async Task Dialogs_pushed_on_mount_and_popped_on_dispose_name_the_open_chain_in_any_closing_order()
    {
        var browser = new TestBrowser("/superadmin/tenants?q=a");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var tenant = Guid.NewGuid();

        var outer = service.PushDialog(new DialogStackSegment("tenant", tenant));
        var inner = service.PushDialog(new DialogStackSegment("role", "7"));
        await service.WhenWrittenAsync();
        Assert.Equal($"tenant-{tenant:D}.role-7", browser.Query(UrlStateKeys.DialogStack));
        Assert.Equal("a", browser.Query("q"));

        outer.Dispose();
        outer.Dispose();
        await service.WhenWrittenAsync();
        Assert.Equal("role-7", browser.Query(UrlStateKeys.DialogStack));
        Assert.Equal([new DialogStackSegment("role", "7")], service.DialogStack);

        inner.Dispose();
        await service.WhenWrittenAsync();
        Assert.Null(browser.Query(UrlStateKeys.DialogStack));
        Assert.Empty(service.DialogStack);
        Assert.Equal("/superadmin/tenants?q=a", browser.PathAndQuery);
    }

    [Fact]
    public async Task A_chain_the_address_arrived_with_is_kept_for_restoring_and_removed_from_the_address()
    {
        var tenant = Guid.NewGuid();
        var browser = new TestBrowser($"/superadmin/tenants?dlg=tenant-{tenant:D}&q=a");
        using var service = browser.CreateService();

        service.MarkInteractive();
        await service.WhenWrittenAsync();

        Assert.Equal("/superadmin/tenants?q=a", browser.PathAndQuery);
        Assert.Equal([new DialogStackSegment("tenant", tenant)], service.TakeArrivedDialogChain());
    }

    [Fact]
    public void The_arrived_chain_is_taken_once_and_kept_until_then_across_a_link_to_the_same_page()
    {
        var tenant = Guid.NewGuid();
        var browser = new TestBrowser($"/superadmin/tenants?dlg=tenant-{tenant:D}");
        using var service = browser.CreateService();

        browser.Navigate("/superadmin/tenants");
        Assert.Equal([new DialogStackSegment("tenant", tenant)], service.TakeArrivedDialogChain());
        Assert.Empty(service.TakeArrivedDialogChain());
    }

    [Fact]
    public void Before_the_chain_is_taken_another_page_drops_it_and_an_address_naming_a_chain_replaces_it()
    {
        var tenant = Guid.NewGuid();
        var dropped = new TestBrowser($"/superadmin/tenants?dlg=tenant-{tenant:D}");
        using (var service = dropped.CreateService())
        {
            dropped.Navigate("/superadmin/errorlog");
            Assert.Empty(service.TakeArrivedDialogChain());
        }

        var replaced = new TestBrowser($"/superadmin/tenants?dlg=tenant-{tenant:D}");
        using (var service = replaced.CreateService())
        {
            replaced.Navigate("/superadmin/tenants?dlg=role-7");
            Assert.Equal([new DialogStackSegment("role", "7")], service.TakeArrivedDialogChain());
        }
    }

    [Fact]
    public async Task Once_the_arrival_is_taken_no_later_address_hands_out_a_chain_so_Back_to_an_entry_with_one_reopens_nothing()
    {
        var tenant = Guid.NewGuid();
        var browser = new TestBrowser("/superadmin/tenants");
        using var service = browser.CreateService();
        service.MarkInteractive();
        Assert.Empty(service.TakeArrivedDialogChain());

        browser.Navigate("/superadmin/errorlog");
        browser.Navigate($"/superadmin/tenants?dlg=tenant-{tenant:D}");
        Assert.Empty(service.TakeArrivedDialogChain());

        await service.WhenWrittenAsync();
        Assert.Null(browser.Query(UrlStateKeys.DialogStack));
        Assert.Equal("/superadmin/tenants", browser.PathAndQuery);
    }

    [Fact]
    public async Task A_dialog_closed_by_a_navigation_leaves_the_new_address()
    {
        var browser = new TestBrowser("/a");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var dialog = service.PushDialog(new DialogStackSegment("tenant", "1"));
        await service.WhenWrittenAsync();

        browser.Navigate("/b?x=1");
        dialog.Dispose();
        await service.WhenWrittenAsync();

        Assert.Equal("/b?x=1", browser.PathAndQuery);
        Assert.Empty(service.DialogStack);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_failed_write_is_not_thrown_and_the_next_write_goes_through()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var status = service.Bind(statusKey);

        browser.Writer.FailNext = true;
        status.Value = StatusFilter.Active;
        await service.WhenWrittenAsync().WaitAsync(cancellationToken);
        Assert.Null(browser.Query("status"));

        status.Value = StatusFilter.Inactive;
        await service.WhenWrittenAsync().WaitAsync(cancellationToken);
        Assert.Equal("inactive", browser.Query("status"));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_failed_write_goes_again_with_the_next_write_of_another_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var browser = new TestBrowser("/list");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var status = service.Bind(statusKey);
        var search = service.Bind(searchKey);

        browser.Writer.FailNext = true;
        status.Value = StatusFilter.Active;
        await service.WhenWrittenAsync().WaitAsync(cancellationToken);
        Assert.Null(browser.Query("status"));

        search.Value = "ann";
        await service.WhenWrittenAsync().WaitAsync(cancellationToken);

        Assert.Equal(("ann", "active"), (browser.Query("q"), browser.Query("status")));
    }

    [Fact]
    public async Task A_write_carries_the_path_as_dotnet_reads_it_with_escaped_letters_decoded()
    {
        var browser = new TestBrowser("/superadmin/error%6Cog");
        using var service = browser.CreateService();
        service.MarkInteractive();

        service.Bind(searchKey).Value = "boom";
        await service.WhenWrittenAsync();

        Assert.Equal("/superadmin/errorlog", Assert.Single(browser.Writer.Writes).Path);
    }

    [Fact]
    public async Task A_text_value_set_to_null_removes_its_key_without_throwing()
    {
        var browser = new TestBrowser("/list?q=ann");
        using var service = browser.CreateService();
        service.MarkInteractive();
        var search = service.Bind(searchKey);

        var thrown = Record.Exception(() => search.Value = null!);
        await service.WhenWrittenAsync();

        Assert.Null(thrown);
        Assert.Null(browser.Query("q"));
    }

    [Fact]
    public async Task A_disposed_service_writes_nothing_more()
    {
        var browser = new TestBrowser("/list");
        var service = browser.CreateService();
        service.MarkInteractive();
        var status = service.Bind(statusKey);
        var dialog = service.PushDialog(new DialogStackSegment("tenant", "1"));
        await service.WhenWrittenAsync();
        var writes = browser.Writer.Writes.Count;

        service.Dispose();
        status.Value = StatusFilter.Active;
        dialog.Dispose();
        status.Dispose();
        browser.Navigate("/other");
        await service.WhenWrittenAsync();

        Assert.Equal(writes, browser.Writer.Writes.Count);
    }
}
