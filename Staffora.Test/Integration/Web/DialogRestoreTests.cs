using System.Collections.Concurrent;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Unit.Architecture.DialogScanSamples;
using Staffora.Test.Web;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class DialogRestoreTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DialogRouteRegistry testRoutes = DialogRouteRegistry.FromTypes(
        [typeof(TestParentDialogContext), typeof(TestParentDialog), typeof(TestChildDialogContext), typeof(TestChildDialog)]);

    private readonly RealDatabase database;

    public DialogRestoreTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task F5_on_a_nested_chain_reopens_the_parent_and_the_child_on_top_in_order_each_loaded_before_it_shows()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(parent), Child(child))}&q=kept");

        var opened = new List<(StateEntityDialogContext Context, StateEntityDialogLoadState State)>();
        circuit.Dialogs.OnOpen += (_, _, parameters, _) =>
        {
            if (parameters.TryGetValue(nameof(StateEntityDialogHost.Session), out var value) && value is StateEntityDialogSession session)
            {
                opened.Add((session.Current.Context, session.Current.Context.LoadState));
            }
        };

        await circuit.RestoreAsync();

        circuit.WaitForDialogs(2);
        Assert.Equal([parent, child], opened.Select(open => open.Context.EntityId!.Value));
        Assert.All(opened, open => Assert.Equal(StateEntityDialogLoadState.Loaded, open.State));
        Assert.IsType<TestParentDialogContext>(opened[0].Context);
        Assert.IsType<TestChildDialogContext>(opened[1].Context);
        Assert.Equal([Parent(parent), Child(child)], circuit.UrlState.DialogStack);
        Assert.Equal(["Parent with a child", "Child"], circuit.Titles());
        Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog__body > [data-loading]"));

        Assert.Equal(TestParentDialogContext.InfoView, opened[0].Context.ActiveViewKey);
        Assert.Equal(opened.Select(open => open.Context), circuit.Page.Services.GetRequiredService<OpenStateEntityDialogs>().All);
        Assert.Empty(circuit.Logs.AtLeast(LogLevel.Warning));
    }

    [Fact]
    public async Task Another_tenants_id_and_a_missing_one_open_nothing_alike_and_stop_the_chain_there()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (theirs, theirChild) = await AddParentWithChildAsync(scenario.B, cancellationToken);
        var (ours, ourChild) = await AddParentWithChildAsync(scenario.A, cancellationToken);

        foreach (var hidden in new[] { theirs, Guid.NewGuid() })
        {
            await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(hidden), Child(ourChild))}");
            await circuit.RestoreAsync();

            Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog"));
            Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog__could-not-open"));
            Assert.Empty(circuit.UrlState.DialogStack);
            Assert.Empty(circuit.Logs.AtLeast(LogLevel.Warning));
        }

        await using (var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(ours), Child(theirChild))}"))
        {
            await circuit.RestoreAsync();

            circuit.WaitForDialogs(1);
            Assert.Equal(["Parent with a child"], circuit.Titles());
            Assert.Equal([Parent(ours)], circuit.UrlState.DialogStack);
            Assert.DoesNotContain("Their", circuit.Dialog.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_deleted_row_opens_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, _) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var deleted = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-delete", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            Assert.True(deleted.IsSuccess, deleted.Error);
        }

        await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(parent))}");
        await circuit.RestoreAsync();

        Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog"));
        Assert.Empty(circuit.UrlState.DialogStack);
    }

    [Fact]
    public async Task A_reader_the_loads_permission_refuses_gets_nothing_opened()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        await using var circuit = Circuit(scenario.A.RuntimeOf(colleague), $"/me/overview?dlg={Chain(Parent(parent), Child(child))}");
        await circuit.RestoreAsync();

        Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog"));
        Assert.Empty(circuit.UrlState.DialogStack);
        Assert.DoesNotContain(TestParentDialogService.NoAccess, circuit.Dialog.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_the_platform_console_after_an_impersonation_ended_a_tenants_ids_open_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await using var circuit = Circuit(platform.PlatformOwnerRuntime(), $"/superadmin/overview?dlg={Chain(Parent(parent), Child(child))}", policies: [SessionPolicies.PlatformConsole]);
        await circuit.RestoreAsync(typeof(Staffora.Blazor.Pages.SuperAdmin.SuperAdminOverview));

        Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog"));
        Assert.Empty(circuit.UrlState.DialogStack);

        var refusal = Assert.Single(circuit.Logs.AtLeast(LogLevel.Information), entry => entry.Contains("could not open", StringComparison.Ordinal));
        Assert.Contains(TestParentDialogService.NotFound, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_naming_a_tenant_reopens_the_tenant_dialog_on_the_tenant_console_and_a_missing_field_stops_the_chain()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var app = DialogRouteRegistry.FromAssemblies(typeof(StateEntityDialogContext).Assembly);
        var tenant = new DialogStackSegment(DialogTokens.Tenant, scenario.A.TenantId);
        var field = new DialogStackSegment(DialogTokens.UserField, Guid.NewGuid());

        await using var circuit = Circuit(platform.PlatformOwnerRuntime(), $"/superadmin/tenants/{scenario.A.TenantId}?dlg={Chain(tenant, field)}", policies: [SessionPolicies.PlatformConsole], routes: app);
        await circuit.RestoreAsync(typeof(Staffora.Blazor.Pages.SuperAdmin.TenantConsole));

        circuit.WaitForDialogs(1);
        Assert.IsType<TenantDialogContext>(Assert.Single(circuit.Contexts()));
        Assert.Equal([scenario.A.Name], circuit.Titles());
        Assert.Equal([tenant], circuit.UrlState.DialogStack);
        Assert.Empty(circuit.Logs.AtLeast(LogLevel.Warning));
        Assert.Equal(typeof(TenantDialog).GetMethod(nameof(TenantDialog.OpenAsync)), app.Find(DialogTokens.Tenant)!.Opener);
        Assert.Equal(typeof(UserFieldDialog).GetMethod(nameof(UserFieldDialog.OpenAsync)), app.Find(DialogTokens.UserField)!.Opener);
        Assert.Null(app.Find("tenantview"));
    }
    [Theory]
    [InlineData("unknown", "nosuchdialog-{guid}.{child}", 0, true)]
    [InlineData("form dialog", "userfield-{guid}.{child}", 0, false)]
    [InlineData("not a key", "testparent-7", 0, true)]
    [InlineData("named twice", "{parent}.{parent}", 1, true)]
    [InlineData("named twice, the key in capitals", "{parent}.{parentInCapitals}", 1, true)]
    [InlineData("named twice, all in capitals", "{parent}.{PARENT}", 1, true)]
    [InlineData("unparsable", "{parent}.%%%.{child}", 1, false)]
    [InlineData("no id", "testparent-", 0, false)]
    [InlineData("empty segments", "..{parent}", 0, false)]
    [InlineData("too long", "{parent}.{child}{padding}", 0, false)]
    public async Task A_chain_the_restore_cannot_read_is_ignored_from_there_without_throwing(string why, string chain, int opened, bool warns)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var value = chain
            .Replace("{parentInCapitals}", $"{TestParentDialogContext.Token}-{parent.ToString("D").ToUpperInvariant()}", StringComparison.Ordinal)
            .Replace("{PARENT}", Parent(parent).ToString().ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("{parent}", Parent(parent).ToString(), StringComparison.Ordinal)
            .Replace("{child}", Child(child).ToString(), StringComparison.Ordinal)
            .Replace("{guid}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal)
            .Replace("{padding}", "." + string.Join('.', Enumerable.Repeat(Child(Guid.NewGuid()).ToString(), 12)), StringComparison.Ordinal);
        await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={value}");

        await circuit.RestoreAsync();

        if (opened > 0)
        {
            circuit.WaitForDialogs(opened);
        }

        Assert.True(circuit.Dialog.FindAll(".state-entity-dialog").Count == opened, why);
        Assert.Empty(circuit.Logs.AtLeast(LogLevel.Error));
        Assert.Equal(warns, circuit.Logs.AtLeast(LogLevel.Warning).Count == 1);
    }

    [Fact]
    public async Task Only_the_arrival_reopens_so_Back_to_an_entry_with_its_chain_and_a_second_layout_open_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var address = $"/me/overview?dlg={Chain(Parent(parent), Child(child))}";
        await using var circuit = Circuit(scenario.A.Runtime(), address);
        await circuit.RestoreAsync();
        circuit.WaitForDialogs(2);

        await circuit.Dialog.InvokeAsync(() => circuit.Navigation.NavigateTo("/me/profile"));
        circuit.WaitForDialogs(0);
        await circuit.Dialog.InvokeAsync(() => circuit.Navigation.NavigateTo(address));
        var second = await circuit.RestoreAsync();
        Assert.True(second.Instance.Restoring.IsCompleted);
        await Task.Delay(200, cancellationToken);

        Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog"));
        Assert.Empty(circuit.UrlState.DialogStack);
        Assert.Empty(circuit.UrlState.TakeArrivedDialogChain());
    }

    [Fact]
    public async Task A_navigation_asks_once_for_every_dialog_of_a_restored_chain_with_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(parent), Child(child))}");
        await circuit.RestoreAsync();
        circuit.WaitForDialogs(2);

        circuit.Dialog.Find("[name=test-parent-name]").Change("Parent renamed");
        circuit.Dialog.Find("[name=test-child-name]").Change("Child renamed");
        circuit.Dialog.WaitForAssertion(() => Assert.Equal(2, circuit.Dialog.FindAll(".state-entity-dialog-header__unsaved").Count), Wait);

        _ = circuit.Dialog.InvokeAsync(() => circuit.Navigation.NavigateTo("/me/profile"));
        circuit.Dialog.WaitForAssertion(() => Assert.Equal(["Discard changes in 2 records?"], circuit.Dialog.FindAll(".form-dialog-header__title").Select(title => title.TextContent)), Wait);
        circuit.ConfirmButton("Keep editing").Click();
        circuit.Dialog.WaitForAssertion(() => Assert.Empty(circuit.Dialog.FindAll(".form-dialog")), Wait);
        Assert.Equal(2, circuit.Dialog.FindAll(".state-entity-dialog").Count);
        Assert.Equal("Child renamed", circuit.Dialog.Find("[name=test-child-name]").GetAttribute("value"));
        Assert.EndsWith("/me/overview", new Uri(circuit.Navigation.Uri).AbsolutePath, StringComparison.Ordinal);

        _ = circuit.Dialog.InvokeAsync(() => circuit.Navigation.NavigateTo("/me/profile"));
        circuit.Dialog.WaitForAssertion(() => Assert.Single(circuit.Dialog.FindAll(".form-dialog-header__title")), Wait);
        bool? onDispatcher = null;
        circuit.Navigation.LocationChanged += (_, _) => onDispatcher = circuit.Page.Renderer.Dispatcher.CheckAccess();
        circuit.ConfirmButton("Discard changes").Click();
        circuit.WaitForDialogs(0);
        Assert.EndsWith("/me/profile", new Uri(circuit.Navigation.Uri).AbsolutePath, StringComparison.Ordinal);
        Assert.True(onDispatcher);
    }

    [Fact]
    public async Task A_restored_child_that_changed_something_reloads_the_dialog_below_it_when_it_closes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(parent), Child(child))}");
        await circuit.RestoreAsync();
        circuit.WaitForDialogs(2);
        var parentContext = circuit.Contexts()[0];
        var versionBefore = parentContext.Version;

        circuit.Dialog.WaitForAssertion(() => Assert.NotNull(circuit.TopDialog().QuerySelector("button[data-transition='test-child-activate']")), Wait);
        circuit.TopDialog().QuerySelector("button[data-transition='test-child-activate']")!.Click();
        circuit.Dialog.WaitForAssertion(() => Assert.Contains("Active", circuit.TopDialog().QuerySelector(".state-pill__name")!.TextContent, StringComparison.Ordinal), Wait);
        circuit.TopDialog().QuerySelector(".state-entity-dialog-header__close")!.Click();

        circuit.WaitForDialogs(1);
        circuit.Dialog.WaitForAssertion(() =>
        {
            Assert.Equal(1, parentContext.ReloadRequests);
            Assert.True(parentContext.Version > versionBefore);
        }, Wait);
    }

    [Fact]
    public async Task An_anonymous_visitor_and_a_page_the_session_may_not_open_reopen_nothing_and_ask_no_service()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var address = $"/me/overview?dlg={Chain(Parent(parent), Child(child))}";

        await using (var anonymous = Circuit(scenario.A.Runtime(), address, authenticated: false))
        {
            anonymous.Parents.LoadThrows = "No load for an anonymous visitor.";
            await anonymous.RestoreAsync(typeof(Staffora.Blazor.Pages.NotFound));
            await AssertNothingAskedAsync(anonymous);
        }

        await using (var refused = Circuit(scenario.A.Runtime(), $"/superadmin/overview?dlg={Chain(Parent(parent), Child(child))}"))
        {
            refused.Parents.LoadThrows = "No load on a page the session may not open.";
            await refused.RestoreAsync(typeof(Staffora.Blazor.Pages.SuperAdmin.SuperAdminOverview));
            await AssertNothingAskedAsync(refused);
        }

        await using (var unrouted = Circuit(scenario.A.Runtime(), address))
        {
            unrouted.Parents.LoadThrows = "No load outside a route.";
            await unrouted.RestoreAsync(page: null);
            await AssertNothingAskedAsync(unrouted);
        }

        await using var allowed = Circuit(scenario.A.Runtime(), address);
        await allowed.RestoreAsync();
        allowed.WaitForDialogs(2);

        static async Task AssertNothingAskedAsync(RestoreCircuit circuit)
        {
            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
            Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog"));
            Assert.Empty(circuit.UrlState.DialogStack);
            Assert.DoesNotContain(circuit.Logs.AtLeast(LogLevel.Information), entry => entry.Contains(nameof(StateEntityDialogRestoreHost), StringComparison.Ordinal));
            Assert.Empty(circuit.UrlState.TakeArrivedDialogChain());
        }
    }

    [Fact]
    public async Task Each_level_is_authorized_by_its_own_load_and_the_chain_is_not_checked_as_a_path()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (first, _) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var (_, otherChild) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var (_, theirChild) = await AddParentWithChildAsync(scenario.B, cancellationToken);

        await using (var unrelated = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(first), Child(otherChild))}"))
        {
            await unrelated.RestoreAsync();
            unrelated.WaitForDialogs(2);
            Assert.Equal([Parent(first), Child(otherChild)], unrelated.UrlState.DialogStack);
        }

        await using (var upsideDown = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Child(otherChild), Parent(first))}"))
        {
            await upsideDown.RestoreAsync();
            upsideDown.WaitForDialogs(2);
            Assert.Equal([Child(otherChild), Parent(first)], upsideDown.UrlState.DialogStack);
        }

        await using (var foreignAbove = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(first), Child(theirChild), Child(otherChild))}"))
        {
            await foreignAbove.RestoreAsync();
            foreignAbove.WaitForDialogs(1);
            Assert.Equal([Parent(first)], foreignAbove.UrlState.DialogStack);
        }

        await using (var foreignBelow = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Child(theirChild), Parent(first))}"))
        {
            await foreignBelow.RestoreAsync();
            Assert.Empty(foreignBelow.Dialog.FindAll(".state-entity-dialog"));
            Assert.Empty(foreignBelow.UrlState.DialogStack);
        }

        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Reader", cancellationToken);
        await using var refused = Circuit(scenario.A.RuntimeOf(colleague), $"/me/overview?dlg={Chain(Child(otherChild), Parent(first))}");
        await refused.RestoreAsync();
        Assert.Empty(refused.Dialog.FindAll(".state-entity-dialog"));
        Assert.Empty(refused.UrlState.DialogStack);
    }

    [Fact]
    public async Task An_opener_that_drops_its_options_opens_past_the_check_and_stops_the_chain_with_a_warning()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var dropping = DialogRouteRegistry.FromTypes([typeof(TestParentDialogContext), typeof(OptionsDroppingSampleBody), typeof(TestChildDialogContext), typeof(TestChildDialog)]);
        await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(parent), Child(child))}", routes: dropping);

        await circuit.RestoreAsync();

        circuit.WaitForDialogs(1);
        await Task.Delay(200, cancellationToken);
        Assert.Single(circuit.Dialog.FindAll(".state-entity-dialog"));
        var warning = Assert.Single(circuit.Logs.AtLeast(LogLevel.Warning));
        Assert.Contains("did not hand its options to the door", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_door_refuses_a_restored_level_in_create_mode_or_over_several_targets()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (first, _) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        var (second, _) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime(), "/me/overview");

        var create = new StateEntityDialogSession([StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(TestParentDialog.ContextFor(circuit.Parents, null))]);
        var several = new StateEntityDialogSession([
            StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(TestParentDialog.ContextFor(circuit.Parents, first)),
            StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(TestParentDialog.ContextFor(circuit.Parents, second))]);
        foreach (var session in new[] { create, several })
        {
            var arrival = circuit.Arrival(CancellationToken.None);
            var admitted = await circuit.Dialog.InvokeAsync(() => arrival.AdmitAsync(session));

            Assert.False(admitted);
            Assert.True(arrival.WasConsulted);
            Assert.Null(await arrival.Outcome.WaitAsync(Wait, cancellationToken));
            Assert.All(session.Targets, target => Assert.NotEqual(StateEntityDialogLoadState.Loaded, target.Context.LoadState));
        }

        var one = new StateEntityDialogSession([StateEntityDialogTarget.For<TestParentDialog, TestParentDialogContext>(TestParentDialog.ContextFor(circuit.Parents, first))]);
        Assert.True(await circuit.Dialog.InvokeAsync(() => circuit.Arrival(CancellationToken.None).AdmitAsync(one)));
        Assert.Equal(StateEntityDialogLoadState.Loaded, one.Current.Context.LoadState);
        one.Current.Context.End();
    }

    [Fact]
    public async Task A_navigation_while_the_chain_reopens_stops_it_before_the_next_level()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(parent), Child(child))}");
        var naming = circuit.Page.JSInterop.SetupModule(StateEntityDialogHeader.ModulePath).SetupVoid("nameDialog", _ => true);
        var opened = new List<Guid>();
        circuit.Dialogs.OnOpen += (_, _, parameters, _) =>
        {
            if (parameters.TryGetValue(nameof(StateEntityDialogHost.Session), out var value) && value is StateEntityDialogSession session)
            {
                opened.Add(session.Current.Context.EntityId!.Value);
            }
        };

        var host = circuit.Render();
        circuit.WaitForDialogs(1);
        Assert.False(host.Instance.Restoring.IsCompleted);
        await circuit.Dialog.InvokeAsync(() => circuit.Navigation.NavigateTo("/me/profile"));
        await host.Instance.Restoring.WaitAsync(Wait, cancellationToken);
        await Task.Delay(200, cancellationToken);

        Assert.Equal([parent], opened);
        Assert.Empty(circuit.Dialog.FindAll(".state-entity-dialog"));
        Assert.Empty(circuit.UrlState.DialogStack);
        naming.SetVoidResult();
    }

    [Theory]
    [InlineData(typeof(Staffora.Blazor.Layout.MainLayout), false)]
    [InlineData(typeof(Staffora.Blazor.Layout.SuperAdminLayout), true)]
    public async Task Each_layout_hosts_the_restore_for_a_signed_in_user_once_the_session_is_resolved(Type layout, bool platformOwner)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var userId = platformOwner
            ? (await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken)).OwnerId
            : (await ScenarioAsync(cancellationToken)).A.OwnerId;
        var runtime = new RuntimeExecutionContext();
        await using var host = ServiceHost.Create(this.database, runtime);
        await using var page = new PageContext();
        page.Services.AddSingleton<IRuntimeExecutionContext>(runtime);
        page.Services.AddSingleton(host.Get<IApplicationDbContextFactory<Staffora.Data.ApplicationDbContext>>());
        page.Services.AddSingleton(TimeProvider.System);
        page.Services.AddScoped<PermissionService>();
        page.Services.AddScoped<ImpersonationAuthorizer>();
        page.Services.AddScoped<ImpersonationAuditWriter>();
        page.Services.AddScoped<UserSessionService>();

        page.Services.AddSingleton(host.Get<Staffora.Services.Messaging.NoticeService>());
        page.Services.AddSingleton(host.Get<Staffora.Services.Search.GlobalSearchService>());
        page.Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        page.Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        page.Services.AddAuthorizationCore();
        page.Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(new SignedIn(SessionCircuit.WithCurrentStamp(this.database, SessionCircuit.Cookie(userId))));

        var rendered = page.Render<Microsoft.AspNetCore.Components.Authorization.CascadingAuthenticationState>(parameters => parameters.AddChildContent(builder =>
        {
            builder.OpenComponent(0, layout);
            builder.AddAttribute(1, nameof(LayoutComponentBase.Body), (RenderFragment)(body => body.AddContent(0, "Page content")));
            builder.CloseComponent();
        }));

        rendered.WaitForAssertion(() =>
        {
            Assert.Contains("Page content", rendered.Markup, StringComparison.Ordinal);
            Assert.Single(rendered.FindComponents<StateEntityDialogRestoreHost>());
        }, Wait);
        Assert.Equal(userId, runtime.UserId);

        if (!platformOwner)
        {
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<Staffora.Blazor.Components.Account.NotificationBell>()), Wait);
        }
    }

    [Fact]
    public void A_context_that_ends_before_its_header_rendered_no_longer_holds_the_level_above()
    {
        var context = TestParentDialog.ContextFor(null!, Guid.NewGuid());
        Assert.False(context.Shown.IsCompleted);

        context.End();

        Assert.True(context.Shown.IsCompleted);
    }

    [Fact]
    public async Task A_page_that_names_a_dialogs_view_reopens_only_the_first_level_on_it_and_a_level_above_on_its_first_view()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var (parent, child) = await AddParentWithChildAsync(scenario.A, cancellationToken);

        await using (var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Parent(parent))}"))
        {
            await circuit.RestoreAsync(typeof(ParentsOnChildrenPage));
            circuit.WaitForDialogs(1);
            circuit.Dialog.WaitForAssertion(() => Assert.Equal(TestParentDialogContext.ChildrenView, circuit.Contexts().Single().ActiveViewKey), Wait);
        }

        await using (var circuit = Circuit(scenario.A.Runtime(), $"/me/overview?dlg={Chain(Child(child), Parent(parent))}"))
        {
            await circuit.RestoreAsync(typeof(ParentsOnChildrenPage));
            circuit.WaitForDialogs(2);
            circuit.Dialog.WaitForAssertion(() =>
            {
                var contexts = circuit.Contexts();
                Assert.IsType<TestParentDialogContext>(contexts[1]);
                Assert.Equal(TestParentDialogContext.InfoView, contexts[1].ActiveViewKey);
            }, Wait);
            Assert.Empty(circuit.Logs.AtLeast(LogLevel.Warning));
        }
    }

    [DialogViewOnPage(TestParentDialogContext.Token, TestParentDialogContext.ChildrenView)]
    private sealed class ParentsOnChildrenPage : ComponentBase;

    private static DialogStackSegment Parent(Guid id) => new(TestParentDialogContext.Token, id);

    private static DialogStackSegment Child(Guid id) => new(TestChildDialogContext.Token, id);

    private static string Chain(params DialogStackSegment[] segments) => DialogStackSegment.FormatChain(segments)!;

    private async Task<TwoTenantScenario> ScenarioAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.ViewPermission, TestParentDialogService.ChildrenPermission);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, TestParentDialogService.ViewPermission, TestParentDialogService.ChildrenPermission);
        return scenario;
    }

    private async Task<(Guid Parent, Guid Child)> AddParentWithChildAsync(ScenarioTenant tenant, CancellationToken cancellationToken)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent and its child as transitions left them");
        var name = tenant.Name.StartsWith("Company B", StringComparison.Ordinal) ? "Their parent" : "Parent with a child";
        var parent = new TestParent { Name = name, State = TestParentState.Draft };
        var child = new TestChild { Name = tenant.Name.StartsWith("Company B", StringComparison.Ordinal) ? "Their child" : "Child of the parent" };
        parent.Children.Add(child);
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return (parent.Id, child.Id);
    }

    private RestoreCircuit Circuit(IRuntimeExecutionContext runtime, string address, bool authenticated = true, string[]? policies = null, DialogRouteRegistry? routes = null) =>
        new(this.database, runtime, address, authenticated, policies ?? [SessionPolicies.TenantPages], routes ?? testRoutes);

    private sealed class RestoreCircuit : IAsyncDisposable
    {
        public static readonly Type DefaultPage = typeof(Staffora.Blazor.Pages.Me.Overview);

        private readonly ServiceHost host;

        public RestoreCircuit(RealDatabase database, IRuntimeExecutionContext runtime, string address, bool authenticated, string[] policies, DialogRouteRegistry routes)
        {
            this.host = ServiceHost.Create(database, runtime, services => services.AddScoped<TestParentDialogService>().AddScoped<TestChildDialogService>());
            Page = new PageContext();
            Page.Services.AddLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Debug));
            Page.Services.AddSingleton(runtime);
            Page.Services.AddSingleton(TimeProvider.System);
            Page.Services.AddSingleton(this.host.Get<PermissionService>());
            Page.Services.AddSingleton(this.host.Get<WorkflowService>());
            Page.Services.AddSingleton(this.host.Get<TestParentDialogService>());
            Page.Services.AddSingleton(this.host.Get<TestChildDialogService>());
            Page.Services.AddSingleton(this.host.Get<TenantService>());
            Page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            Page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            Page.Services.AddSingleton(this.host.Get<Staffora.Services.CustomFields.CustomFieldService>());
            Page.Services.AddSingleton(routes);

            var authorization = Page.AddAuthorization();
            if (authenticated)
            {
                authorization.SetAuthorized(runtime.UserId?.ToString() ?? "user");
                authorization.SetPolicies(policies);
            }

            Page.Services.GetRequiredService<NavigationManager>().NavigateTo(address);
            Dialog = Page.Render<RadzenDialog>();
        }

        public LogRecorder Logs { get; } = new();

        public PageContext Page { get; }

        public IRenderedComponent<RadzenDialog> Dialog { get; }

        public DialogService Dialogs => Page.Services.GetRequiredService<DialogService>();

        public UrlStateService UrlState => Page.Services.GetRequiredService<UrlStateService>();

        public NavigationManager Navigation => Page.Services.GetRequiredService<NavigationManager>();

        public TestParentDialogService Parents => this.host.Get<TestParentDialogService>();

        public async Task<IRenderedComponent<StateEntityDialogRestoreHost>> RestoreAsync(Type? page)
        {
            var host = Render(page);
            await host.Instance.Restoring.WaitAsync(Wait);
            return host;
        }

        public Task<IRenderedComponent<StateEntityDialogRestoreHost>> RestoreAsync() => RestoreAsync(DefaultPage);

        public IRenderedComponent<StateEntityDialogRestoreHost> Render(Type? page)
        {
            if (page is null)
            {
                return Page.Render<StateEntityDialogRestoreHost>();
            }

            var route = new RouteData(page, new Dictionary<string, object?>());
            return Page.Render<CascadingValue<RouteData>>(parameters => parameters
                .Add(cascade => cascade.Value, route)
                .AddChildContent<StateEntityDialogRestoreHost>())
                .FindComponent<StateEntityDialogRestoreHost>();
        }

        public IRenderedComponent<StateEntityDialogRestoreHost> Render() => Render(DefaultPage);

        public StateEntityDialogArrival Arrival(CancellationToken cancellation) =>
            new(Page.Services.GetRequiredService<WorkflowService>(), TimeProvider.System, Page.Services, Page.Services.GetRequiredService<ILogger<DialogRestoreTests>>(), Dialogs, cancellation);

        public void WaitForDialogs(int count) =>
            Dialog.WaitForAssertion(() => Assert.Equal(count, Dialog.FindAll(".state-entity-dialog").Count), Wait);

        public List<StateEntityDialogContext> Contexts() => [.. Dialog.FindComponents<StateEntityDialogLayout>().Select(layout => layout.Instance.Context)];

        public List<string> Titles() => [.. Dialog.FindAll(".state-entity-dialog-header__title").Select(title => title.TextContent)];

        public IElement TopDialog() => Dialog.FindAll(".state-entity-dialog").Last();

        public IElement ConfirmButton(string text) =>
            Dialog.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await Page.DisposeAsync();
            await this.host.DisposeAsync();
        }
    }

    private sealed class PageContext : WebTestContext;

    private sealed class SignedIn : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        private readonly System.Security.Claims.ClaimsPrincipal principal;

        public SignedIn(System.Security.Claims.ClaimsPrincipal principal)
        {
            this.principal = principal;
        }

        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(this.principal));
    }

    private sealed class LogRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> entries = new();

        public IReadOnlyList<string> AtLeast(LogLevel level) => [.. this.entries.Where(entry => entry.Level >= level).Select(entry => $"{entry.Level} {entry.Category}: {entry.Message}")];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly LogRecorder recorder;
            private readonly string category;

            public Logger(LogRecorder recorder, string category)
            {
                this.recorder = recorder;
                this.category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                this.recorder.entries.Enqueue((logLevel, this.category, formatter(state, exception)));
        }
    }
}
