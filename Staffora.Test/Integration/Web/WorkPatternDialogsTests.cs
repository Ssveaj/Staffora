using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class WorkPatternDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public WorkPatternDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task An_admin_publishes_a_weekly_pattern_from_the_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, admin) = await ArrangeAsync("Dialog Publishing Staffing", cancellationToken);
        await using var circuit = await CircuitAsync(admin);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<WorkPatterns>(builder));
        rendered.WaitForAssertion(() => Assert.Contains("No work pattern yet", rendered.Markup, StringComparison.Ordinal), Wait);

        rendered.FindAll("button").Single(button => TextOf(button) == "New pattern").Click();
        WaitForDialog(rendered);
        Assert.Equal(7, rendered.FindAll(".state-pattern-segments__day").Count);
        rendered.Find("[name=pattern-name]").Change("Full time");

        Assert.Empty(rendered.FindAll(".state-entity-dialog-footer__actions"));
        Assert.Empty(rendered.FindAll("[data-copy-pattern]"));

        Assert.Equal("polite", rendered.Find("[data-problems-region]").GetAttribute("aria-live"));
        Assert.Empty(rendered.FindAll("[data-problems-region] [data-problems]"));

        for (var day = 0; day < 5; day++)
        {
            rendered.Find($"[data-add-day='{day}']").Click();
        }

        rendered.Find("[name=pattern-segment-4-1-end]").Change("12:00");

        rendered.Find("[name=pattern-segment-0-1-start]").Change("");
        rendered.WaitForAssertion(() => Assert.Contains("Monday: the start of 08:00–16:00 is empty. It stays 08:00 until a time is typed.", rendered.Find("[data-problems-region] [data-problems]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("true", rendered.Find("[name=pattern-segment-0-1-start]").GetAttribute("aria-invalid"));
        rendered.Find("[name=pattern-segment-0-1-start]").Change("08:00");
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll("[data-problems]")), Wait);
        Assert.Null(rendered.Find("[name=pattern-segment-0-1-start]").GetAttribute("aria-invalid"));

        rendered.Find("[name=pattern-segment-0-1-end]").Change("23:59");
        rendered.Find("[data-add-day='0']").Click();
        rendered.WaitForAssertion(() => Assert.Equal("00:00", rendered.Find("[name=pattern-segment-0-2-start]").GetAttribute("value")), Wait);
        Assert.Equal("01:00", rendered.Find("[name=pattern-segment-0-2-end]").GetAttribute("value"));
        Assert.Empty(rendered.FindAll("[data-problems]"));
        rendered.FindAll(".state-pattern-segments__segment").Single(segment => segment.GetAttribute("data-segment") == "pattern-segment-0-2").QuerySelector("button")!.Click();
        rendered.Find("[name=pattern-segment-0-1-end]").Change("16:00");
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-pattern-segments__day[data-day='0'] .state-pattern-segments__segment")), Wait);

        rendered.Find("[data-add-day='0']").Click();
        rendered.Find("[name=pattern-segment-0-2-start]").Change("15:00");
        rendered.WaitForAssertion(() => Assert.Contains("Monday: 08:00-16:00 and 15:00-17:00 overlap.", rendered.Find("[data-problems-region][aria-live=polite] [data-problems]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition='pattern-save']"));
        rendered.FindAll(".state-pattern-segments__segment").Last(segment => segment.GetAttribute("data-segment") == "pattern-segment-0-2").QuerySelector("button")!.Click();
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll("[data-problems]")), Wait);

        rendered.WaitForAssertion(() => rendered.Find(".state-entity-dialog button[data-transition='pattern-save']").Click(), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("Draft", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);

        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog-footer__actions [data-copy-pattern]")), Wait);
        rendered.WaitForAssertion(() => rendered.Find(".state-entity-dialog button[data-transition='pattern-publish']").Click(), Wait);
        rendered.WaitForAssertion(() => ConfirmButton(rendered, "Publish").Click(), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("Published", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
        Assert.Empty(rendered.FindAll(".state-pattern-segments input"));
        Assert.Equal("read-only", rendered.Find(".state-pattern-segments").GetAttribute("data-access"));

        Assert.Single(rendered.FindAll("[data-copy-pattern]"));
        Assert.Equal("08:00–12:00", rendered.Find(".state-pattern-segments__day[data-day='4'] [data-hours]").TextContent.Trim());
        Assert.Equal("Not working", rendered.Find(".state-pattern-segments__day[data-day='5'] .state-pattern-segments__off").TextContent.Trim());

        var stored = Assert.Single(await StoredAsync(scenario.TenantId, cancellationToken));
        Assert.Equal(("Full time", WorkPatternState.Published, 1), (stored.Name, stored.State, stored.CycleWeeks));
        Assert.Equal([0, 1, 2, 3, 4], stored.Segments.Select(segment => segment.DayIndex).Order());
        Assert.Equal(new TimeOnly(12, 0), stored.Segments.Single(segment => segment.DayIndex == 4).End);
        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        rendered.WaitForAssertion(() => Assert.Contains("36 h per week", rendered.Find("tr.list-row").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Deleting_a_draft_from_its_dialog_removes_it_with_its_segments_and_none_of_the_edits_cancelled_first()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, admin) = await ArrangeAsync("Dialog Deleting Staffing", cancellationToken);
        var id = await CreateDraftAsync(scenario, admin, cancellationToken);
        Assert.Equal(3, await SegmentCountAsync(id, cancellationToken));
        await using var circuit = await CircuitAsync(admin);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        var opened = rendered.InvokeAsync(() => WorkPatternDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<WorkPatternService>(), id));
        WaitForDialog(rendered);

        rendered.Edit();
        rendered.Find("[name=pattern-name]").Change("");
        rendered.Find("[data-add-day='5']").Click();
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-pattern-segments__day[data-day='5'] .state-pattern-segments__segment")), Wait);
        Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition='pattern-delete']"));

        rendered.FindAll(".state-entity-dialog-footer button[data-dialog-action='cancel']").Single().Click();
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-pattern-segments__day[data-day='5'] .state-pattern-segments__segment")), Wait);
        rendered.WaitForAssertion(() => rendered.Find(".state-entity-dialog button[data-transition='pattern-delete']").Click(), Wait);
        rendered.WaitForAssertion(() => ConfirmButton(rendered, "Delete").Click(), Wait);
        await Task.WhenAny(opened, Task.Delay(Wait, cancellationToken));
        Assert.True(opened.IsCompleted, "The Delete was refused: " + string.Join(" ", rendered.FindAll(".state-entity-dialog-footer__error").Select(error => error.TextContent.Trim())));
        var result = await opened;
        Assert.True(result.Deleted);

        Assert.Empty(await StoredAsync(scenario.TenantId, cancellationToken));
        Assert.Equal(0, await SegmentCountAsync(id, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Copy_to_new_draft_opens_a_draft_of_the_published_hours_and_leaves_the_published_pattern_as_it_is()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, admin) = await ArrangeAsync("Dialog Copying Staffing", cancellationToken);
        var id = await CreateDraftAsync(scenario, admin, cancellationToken);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(admin)))
        {
            var patterns = host.Get<WorkPatternService>();
            Assert.True((await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(id, cancellationToken)).Value!, WorkPatternWorkflowAttribute.PublishStep, null, cancellationToken)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(admin);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => WorkPatternDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<WorkPatternService>(), id));
        WaitForDialog(rendered);

        Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition='pattern-save']"));
        rendered.Find("[data-copy-pattern]").Click();
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
        WaitForDialog(rendered);
        var copy = rendered.FindAll(".state-entity-dialog").Last();
        Assert.Equal("Early week (copy)", copy.QuerySelector(".state-entity-dialog-header__title")!.TextContent.Trim());
        Assert.Single(await StoredAsync(scenario.TenantId, cancellationToken));
        Assert.Equal("Early week (copy)", copy.QuerySelector("[name=pattern-name]")!.GetAttribute("value"));
        Assert.Equal(3, copy.QuerySelectorAll(".state-pattern-segments__segment").Length);

        rendered.Find("[name=pattern-segment-1-1-end]").Change("11:00");
        rendered.WaitForAssertion(() => rendered.FindAll(".state-entity-dialog").Last().QuerySelector("button[data-transition='pattern-save']")!.Click(), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("Draft", rendered.FindAll(".state-pill__button").Last().GetAttribute("data-state")), Wait);

        rendered.WaitForAssertion(() => Assert.NotNull(rendered.FindComponents<WorkPatternDialog>().Last().Instance.Context.EntityId), Wait);
        WaitForDialog(rendered);
        var stored = await StoredAsync(scenario.TenantId, cancellationToken);
        Assert.Equal(2, stored.Count);
        var published = stored.Single(pattern => pattern.Id == id);
        var draft = stored.Single(pattern => pattern.Id != id);
        Assert.Equal((WorkPatternState.Published, new TimeOnly(16, 0)), (published.State, published.Segments.Single(segment => segment.DayIndex == 1).End));
        Assert.Equal((WorkPatternState.Draft, "Early week (copy)", new TimeOnly(11, 0)), (draft.State, draft.Name, draft.Segments.Single(segment => segment.DayIndex == 1).End));
        Assert.Equal(3, draft.Segments.Count);
    }

    private async Task<(StaffScenario Scenario, Guid Admin)> ArrangeAsync(string name, CancellationToken cancellationToken)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, name);
        var admin = await scenario.AddPersonAsync(this.database, "Alex", cancellationToken);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(admin, scenario.RootId, scenario.Onboarded.Roles.Administrator, scenario.TenantId)).IsSuccess);
        return (scenario, admin);
    }

    private async Task<Guid> CreateDraftAsync(StaffScenario scenario, Guid admin, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(admin));
        var pattern = new WorkPattern { Name = "Early week", CycleWeeks = 1 };
        for (var day = 0; day < 3; day++)
        {
            pattern.Segments.Add(new WorkPatternSegment { DayIndex = day, Start = new TimeOnly(8, 0), End = new TimeOnly(16, 0) });
        }

        var created = await host.Get<WorkPatternService>().CreateWorkPatternAsync(pattern, WorkPatternWorkflowAttribute.SaveStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<List<WorkPattern>> StoredAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.WorkPatterns.AsNoTracking().Include(pattern => pattern.Segments).Where(pattern => pattern.TenantId == tenantId).ToListAsync(cancellationToken);
    }

    private async Task<int> SegmentCountAsync(Guid patternId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.WorkPatternSegments.CountAsync(segment => segment.WorkPatternId == patternId, cancellationToken);
    }

    private static IElement ConfirmButton(IRenderedComponent<IComponent> rendered, string text) =>
        rendered.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static IRenderedComponent<IComponent> HostDialogs(PageContext page) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
    });

    private static void Host<TPage>(RenderTreeBuilder builder) where TPage : IComponent
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
        builder.CloseComponent();
        builder.OpenComponent<TPage>(1);
        builder.CloseComponent();
    }

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
    }

    private async Task<Circuit> CircuitAsync(Guid userId)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId));
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime);
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly RuntimeExecutionContext runtime;

        public Circuit(ServiceHost host, RuntimeExecutionContext runtime)
        {
            this.host = host;
            this.runtime = runtime;
        }

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(TimeProvider.System);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<WorkPatternService>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
