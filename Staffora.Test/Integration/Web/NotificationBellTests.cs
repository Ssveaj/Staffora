using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Account;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class NotificationBellTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DialogRouteRegistry routes = DialogRouteRegistry.FromTypes(
        [typeof(TestParentDialogContext), typeof(TestParentDialog), typeof(TestChildDialogContext), typeof(TestChildDialog)]);

    private readonly RealDatabase database;

    public NotificationBellTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_second_users_badge_rises_without_a_reload()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var parent = await AddParentAsync(scenario.A, cancellationToken);

        await using var owner = new Circuit(this.database, scenario.A.Runtime());
        var bell = owner.RenderBell();
        bell.WaitForAssertion(() => Assert.Equal("0", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        Assert.Empty(bell.FindAll(".notice-bell__badge"));
        Assert.Equal("Notifications", bell.Find(".notice-bell__trigger").GetAttribute("aria-label"));

        await using (var actor = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague), services => services.AddSingleton(owner.Bus)))
        {
            var activated = await actor.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-activate", cancellationToken: cancellationToken);
            Assert.True(activated.IsSuccess, activated.Error);
        }

        bell.WaitForAssertion(() => Assert.Equal("1", bell.Find(".notice-bell__badge").TextContent.Trim()), Wait);
        Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count"));
        Assert.Equal("Notifications, 1 unread", bell.Find(".notice-bell__trigger").GetAttribute("aria-label"));
        Assert.Equal("New notice. 1 unread.", bell.Find("[data-notice-announcement]").TextContent.Trim());
        Assert.Equal("polite", bell.Find("[data-notice-announcement]").GetAttribute("aria-live"));
    }

    [Fact]
    public async Task A_row_click_marks_the_notice_read_and_opens_its_entity()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var parent = await AddParentAsync(scenario.A, cancellationToken);
        await ActivateAsync(scenario.A, parent, cancellationToken);

        await using var owner = new Circuit(this.database, scenario.A.Runtime());
        var bell = owner.RenderBell();
        bell.WaitForAssertion(() => Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);

        bell.Find(".notice-bell__trigger").Click();
        bell.WaitForAssertion(() => Assert.Single(bell.FindAll(".notice-row--unread")), Wait);
        Assert.Equal("true", bell.Find(".notice-bell__trigger").GetAttribute("aria-expanded"));
        Assert.Contains("Parent is active", bell.Find(".notice-row").TextContent, StringComparison.Ordinal);

        bell.Find(".notice-row").Click();

        owner.Dialog.WaitForAssertion(() => Assert.Equal(["Parent"], owner.Dialog.FindAll(".state-entity-dialog-header__title").Select(title => title.TextContent)), Wait);
        Assert.Equal(parent, Assert.Single(owner.Dialog.FindComponents<StateEntityDialogLayout>()).Instance.Context.EntityId);
        bell.WaitForAssertion(() => Assert.Equal("0", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        Assert.Empty(bell.FindAll("[data-notice-panel]"));
        Assert.True(await IsReadAsync(scenario.A.OwnerId, cancellationToken));
    }

    [Fact]
    public async Task The_panel_shows_unread_or_all_and_marks_all_read()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await ActivateAsync(scenario.A, await AddParentAsync(scenario.A, cancellationToken), cancellationToken);
        await ActivateAsync(scenario.A, await AddParentAsync(scenario.A, cancellationToken), cancellationToken);

        await using var owner = new Circuit(this.database, scenario.A.Runtime());
        var bell = owner.RenderBell();
        bell.WaitForAssertion(() => Assert.Equal("2", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        bell.Find(".notice-bell__trigger").Click();
        bell.WaitForAssertion(() => Assert.Equal(2, bell.FindAll(".notice-row--unread").Count), Wait);
        Assert.Equal("true", Filter(bell, "Unread").GetAttribute("aria-pressed"));

        bell.Find(".notice-panel__mark-all").Click();

        bell.WaitForAssertion(() => Assert.Contains("You have no unread notices.", bell.Find("[data-notice-panel]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(bell.FindAll(".notice-bell__badge"));
        Assert.Empty(bell.FindAll(".notice-panel__mark-all"));

        bell.WaitForAssertion(() => Assert.Equal(Reference(bell, "title"), owner.LastFocus()), Wait);
        Filter(bell, "All").Click();
        bell.WaitForAssertion(() => Assert.Equal(2, bell.FindAll(".notice-row").Count), Wait);
        Assert.Empty(bell.FindAll(".notice-row--unread"));
        Assert.Equal("true", Filter(bell, "All").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task A_notice_is_announced_even_when_another_page_marked_the_count_down_meanwhile()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await ActivateAsync(scenario.A, await AddParentAsync(scenario.A, cancellationToken), cancellationToken);
        var parent = await AddParentAsync(scenario.A, cancellationToken);

        await using var owner = new Circuit(this.database, scenario.A.Runtime());
        var bell = owner.RenderBell();
        bell.WaitForAssertion(() => Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        await using (var otherPage = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.True((await otherPage.Get<NoticeService>().MarkAllReadAsync(cancellationToken)).IsSuccess);
        }

        await using (var actor = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague), services => services.AddSingleton(owner.Bus)))
        {
            var activated = await actor.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-activate", cancellationToken: cancellationToken);
            Assert.True(activated.IsSuccess, activated.Error);
        }

        bell.WaitForAssertion(() => Assert.Equal("New notice. 1 unread.", bell.Find("[data-notice-announcement]").TextContent.Trim()), Wait);
        Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count"));
    }

    [Fact]
    public async Task Notices_another_step_marked_read_lower_the_badge_without_a_reload_and_without_an_announcement()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var parent = await AddParentAsync(scenario.A, cancellationToken);
        await ActivateAsync(scenario.A, parent, cancellationToken);

        await using var owner = new Circuit(this.database, scenario.A.Runtime());
        var bell = owner.RenderBell();
        bell.WaitForAssertion(() => Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        var announced = bell.Find("[data-notice-announcement]").TextContent.Trim();

        await using (var step = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddSingleton(owner.Bus)))
        {
            await using var context = this.database.CreateContext(scenario.A.Runtime());
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var notices = step.Get<Staffora.Core.Messaging.IHandledNotices>();
            var readers = await notices.MarkReadAsync(context, scenario.A.TenantId, typeof(TestParent), parent, [TestParent.Activated], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Assert.Equal([scenario.A.OwnerId], readers);
            notices.TellReaders(scenario.A.TenantId, readers);
        }

        bell.WaitForAssertion(() => Assert.Equal("0", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        Assert.Empty(bell.FindAll(".notice-bell__badge"));
        Assert.Equal(announced, bell.Find("[data-notice-announcement]").TextContent.Trim());
        Assert.True(await IsReadAsync(scenario.A.OwnerId, cancellationToken));
    }

    [Fact]
    public async Task Escape_on_the_bell_closes_its_open_panel_and_keeps_the_focus_on_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);

        await using var owner = new Circuit(this.database, scenario.A.Runtime());
        var bell = owner.RenderBell();
        bell.WaitForAssertion(() => Assert.Single(bell.FindAll(".notice-bell__trigger")), Wait);
        bell.Find(".notice-bell__trigger").Click();
        bell.WaitForAssertion(() => Assert.Single(bell.FindAll("[data-notice-panel]")), Wait);

        bell.Find(".notice-bell__trigger").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        bell.WaitForAssertion(() => Assert.Empty(bell.FindAll("[data-notice-panel]")), Wait);
        Assert.Equal("false", bell.Find(".notice-bell__trigger").GetAttribute("aria-expanded"));
        Assert.Equal(Reference(bell, "trigger"), owner.LastFocus());
    }

    [Fact]
    public async Task An_impersonating_platform_owner_sees_the_persons_notices_and_marks_none_read()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var parent = await AddParentAsync(scenario.A, cancellationToken);
        await ActivateAsync(scenario.A, parent, cancellationToken);

        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        await using var owner = new Circuit(this.database, impersonating);
        var bell = owner.RenderBell();
        bell.WaitForAssertion(() => Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        bell.Find(".notice-bell__trigger").Click();
        bell.WaitForAssertion(() => Assert.Single(bell.FindAll(".notice-row--unread")), Wait);

        Assert.Empty(bell.FindAll(".notice-panel__mark-all"));
        Assert.Contains(NoticeService.ImpersonatingRefusal, bell.Find("[data-notice-panel]").TextContent, StringComparison.Ordinal);
        bell.Find(".notice-row").Click();
        owner.Dialog.WaitForAssertion(() => Assert.Single(owner.Dialog.FindAll(".state-entity-dialog")), Wait);
        Assert.False(await IsReadAsync(scenario.A.OwnerId, cancellationToken));
        Assert.False((await owner.Notices.MarkAllReadAsync(cancellationToken)).IsSuccess);
        Assert.False(await IsReadAsync(scenario.A.OwnerId, cancellationToken));
    }

    private static string Reference(IRenderedComponent<NotificationBell> bell, string field) =>
        ((ElementReference)typeof(NotificationBell).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(bell.Instance)!).Id;

    private static AngleSharp.Dom.IElement Filter(IRenderedComponent<NotificationBell> bell, string name) =>
        bell.FindAll(".notice-panel__filter").Single(button => button.TextContent.Trim() == name);

    private async Task<TwoTenantScenario> ScenarioAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, TestParentDialogService.ViewPermission, TestParentDialogService.ChildrenPermission);
        return scenario;
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, CancellationToken cancellationToken)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", State = TestParentState.Draft };
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task ActivateAsync(ScenarioTenant tenant, Guid parent, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, tenant.Runtime());
        var activated = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, "test-parent-activate", cancellationToken: cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);
    }

    private async Task<bool> IsReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.MessageRecipients.AsNoTracking().Where(copy => copy.UserId == userId).AllAsync(copy => copy.IsRead, cancellationToken);
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly PageContext page = new();

        public Circuit(RealDatabase database, IRuntimeExecutionContext runtime)
        {
            this.host = ServiceHost.Create(database, runtime, services => services.AddScoped<TestParentDialogService>().AddScoped<TestChildDialogService>());
            this.page.Services.AddSingleton(runtime);
            this.page.Services.AddSingleton(TimeProvider.System);
            this.page.Services.AddSingleton(this.host.Get<PermissionService>());
            this.page.Services.AddSingleton(this.host.Get<WorkflowService>());
            this.page.Services.AddSingleton(this.host.Get<NoticeService>());
            this.page.Services.AddSingleton(this.host.Get<TestParentDialogService>());
            this.page.Services.AddSingleton(this.host.Get<TestChildDialogService>());
            this.page.Services.AddSingleton(routes);
            var authorization = this.page.AddAuthorization();
            authorization.SetAuthorized(runtime.EffectiveUserId?.ToString() ?? "user");
            this.page.Services.GetRequiredService<NavigationManager>().NavigateTo("/me/overview");
            Dialog = this.page.Render<RadzenDialog>();
        }

        public IRenderedComponent<RadzenDialog> Dialog { get; }

        public NoticeBus Bus => this.host.Get<NoticeBus>();

        public NoticeService Notices => this.host.Get<NoticeService>();

        public string? LastFocus() => this.page.JSInterop.Invocations["Blazor._internal.domWrapper.focus"] is [.., var last] && last.Arguments[0] is ElementReference element ? element.Id : null;

        public IRenderedComponent<NotificationBell> RenderBell() => this.page.Render<NotificationBell>();

        public async ValueTask DisposeAsync()
        {
            await this.page.DisposeAsync();
            await this.host.DisposeAsync();
        }
    }

    private sealed class PageContext : WebTestContext;
}
