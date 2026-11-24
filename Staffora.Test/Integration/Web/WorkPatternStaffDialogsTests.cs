using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class WorkPatternStaffDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 11, 18);

    private readonly RealDatabase database;

    public WorkPatternStaffDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_Staff_view_lists_who_the_pattern_is_given_to_and_a_row_opens_the_assignment()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Pattern Staff View", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        var fullTime = await scenario.PublishPatternAsync(this.database, "Full time", ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        await ActiveAsync(scenario, kim, fullTime, Today.AddDays(-16), ct);

        await using var circuit = await CircuitAsync(scenario.Admin);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => WorkPatternDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<WorkPatternService>(), fullTime));
        WaitForDialog(rendered);

        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staff]"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
        var row = rendered.Find(".state-entity-dialog tr.list-row");
        Assert.Contains("Kim Example", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("2026-11-02 until further notice", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("Current", row.QuerySelector("[data-pattern-when]")!.TextContent.Trim());
        Assert.Contains(rendered.FindAll(".state-entity-dialog button"), button => TextOf(button) == "Assign to staff");

        ClickOnce(rendered, () => rendered.Find(".state-entity-dialog tr.list-row button.list-row__open"));
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
        WaitForDialog(rendered);
        var top = rendered.FindAll(".state-entity-dialog").Last();
        Assert.Contains("Kim Example", top.QuerySelector("[data-staff]")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(top.QuerySelector($"button[data-transition='{ResourcePatternAssignmentWorkflowAttribute.DeactivateStep}']"));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Assign_to_staff_is_offered_only_to_an_assigner_on_a_published_pattern()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Pattern Staff Offer", ct);
        var published = await scenario.PublishPatternAsync(this.database, "Full time", ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        var maker = await scenario.StaffMembers.AddPersonAsync(this.database, "Maker", ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, maker, ct, RolePermission.ManageWorkPatterns, RolePermission.ResourcesView);

        await using (var circuit = await CircuitAsync(maker))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => WorkPatternDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<WorkPatternService>(), published));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staff]"));
            rendered.WaitForAssertion(() => Assert.Contains("Nobody works by this pattern", rendered.Markup, StringComparison.Ordinal), Wait);
            Assert.DoesNotContain(rendered.FindAll(".state-entity-dialog button"), button => TextOf(button) == "Assign to staff");
        }

        var drafter = await scenario.StaffMembers.AddPersonAsync(this.database, "Drafter", ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, drafter, ct, RolePermission.ManageWorkPatterns);
        await using (var circuit = await CircuitAsync(drafter))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => WorkPatternDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<WorkPatternService>(), published));
            WaitForDialog(rendered);
            Assert.Empty(rendered.FindAll("[role=tab][data-view=staff]"));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_form_names_each_staff_member_refused_and_assigns_several_at_once()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScheduleScenario.CreateAsync(this.database, "Pattern Staff Form", ct);
        var kim = await scenario.AddStaffAsync(this.database, "Kim", ct);
        var sam = await scenario.AddStaffAsync(this.database, "Sam", ct);
        var lee = await scenario.AddStaffAsync(this.database, "Lee", ct);
        var fullTime = await scenario.PublishPatternAsync(this.database, "Full time", ct, ScheduleScenario.Weekdays(new TimeOnly(8, 0), new TimeOnly(16, 0)));
        var evenings = await scenario.PublishPatternAsync(this.database, "Evenings", ct, ScheduleScenario.Weekdays(new TimeOnly(16, 0), new TimeOnly(20, 0)));
        await ActiveAsync(scenario, kim, fullTime, Today.AddDays(-16), ct);
        var resources = new Dictionary<Guid, Guid>
        {
            [kim] = await ScheduleScenario.ResourceOfAsync(this.database, kim, ct),
            [sam] = await ScheduleScenario.ResourceOfAsync(this.database, sam, ct),
            [lee] = await ScheduleScenario.ResourceOfAsync(this.database, lee, ct),
        };

        await using var circuit = await CircuitAsync(scenario.Admin);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => WorkPatternDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<WorkPatternService>(), evenings));
        WaitForDialog(rendered);
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staff]"));
        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").Single(button => TextOf(button) == "Assign to staff"));

        rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<AssignPatternToStaffDialog>()), Wait);
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<RadzenDropDown<IEnumerable<Guid>>>()), Wait);
        var form = rendered.FindComponent<AssignPatternToStaffDialog>();
        var staff = rendered.FindComponent<RadzenDropDown<IEnumerable<Guid>>>();
        var offered = Assert.IsAssignableFrom<IEnumerable<Staffora.Core.Models.PatternStaffChoice>>(staff.Instance.Data).Select(choice => choice.Label).ToList();
        Assert.Contains("Kim Example (Full time · since 2026-11-02)", offered);
        Assert.Contains("Sam Example (No work pattern)", offered);
        Assert.Equal(Today, rendered.FindComponents<RadzenDatePicker<DateOnly?>>().Single(picker => picker.Instance.Name == "assign-pattern-from").Instance.Value);
        Assert.True(AssignButton(rendered).HasAttribute("disabled"));

        await rendered.InvokeAsync(() => staff.Instance.ValueChanged.InvokeAsync([resources[kim], resources[sam]]));
        rendered.WaitForAssertion(() => Assert.Equal("2", form.Find("[data-chosen-count]").GetAttribute("data-chosen-count")), Wait);
        ClickOnce(rendered, () => AssignButton(rendered));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("[data-assign-error] [data-refused]")), Wait);
        var refused = rendered.Find("[data-assign-error] [data-refused]");
        Assert.Equal(resources[kim].ToString(), refused.GetAttribute("data-refused"));
        Assert.Contains("Kim Example", refused.TextContent, StringComparison.Ordinal);
        Assert.Contains("It overlaps the active assignment of Full time from 2026-11-02 until further notice", refused.TextContent, StringComparison.Ordinal);
        Assert.Equal(0, await CountOfAsync(evenings, ct));

        await rendered.InvokeAsync(() => staff.Instance.ValueChanged.InvokeAsync([resources[sam], resources[lee]]));
        ClickOnce(rendered, () => AssignButton(rendered));
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindComponents<AssignPatternToStaffDialog>()), Wait);
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
        Assert.Equal(["Lee Example", "Sam Example"], rendered.FindAll(".state-entity-dialog tr.list-row").Select(row => row.QuerySelector("button.list-row__open")!.TextContent.Trim()).Order(StringComparer.Ordinal));
        Assert.Equal(2, await CountOfAsync(evenings, ct));

        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").Single(button => TextOf(button) == "Assign to staff"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<AssignPatternToStaffDialog>()), Wait);
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<RadzenDropDown<IEnumerable<Guid>>>()), Wait);
        var again = rendered.FindComponent<RadzenDropDown<IEnumerable<Guid>>>();
        var choices = Assert.IsAssignableFrom<IEnumerable<Staffora.Core.Models.PatternStaffChoice>>(again.Instance.Data).ToList();
        Guid[] holders = [.. new[] { resources[lee], resources[sam] }.Order()];
        Assert.Equal(holders, choices.Where(choice => choice.HasThisPattern).Select(choice => choice.ResourceId).Order());
        Assert.Contains($"Sam Example ({Staffora.Core.Models.PatternStaffChoice.HasThisPatternLabel})", choices.Select(choice => choice.Label));
        Assert.Equal(holders, again.Instance.Value!.Order());
        await rendered.InvokeAsync(() => again.Instance.ValueChanged.InvokeAsync(null!));
        rendered.WaitForAssertion(() => Assert.Equal("0", rendered.FindComponent<AssignPatternToStaffDialog>().Find("[data-chosen-count]").GetAttribute("data-chosen-count")), Wait);
        Assert.True(AssignButton(rendered).HasAttribute("disabled"));
    }

    private static IElement AssignButton(IRenderedComponent<IComponent> rendered) =>
        rendered.FindAll(".form-dialog button").Single(button => TextOf(button) == "Assign");

    private async Task ActiveAsync(ScheduleScenario scenario, Guid user, Guid patternId, DateOnly from, CancellationToken ct)
    {
        await using var admin = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Admin));
        var assignments = admin.Get<PatternAssignmentService>();
        var id = (await ScheduleScenario.CreateAssignmentAsync(assignments, user, patternId, from, null, ct)).Value;
        Assert.True((await ScheduleScenario.StepAsync(assignments, id, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, ct)).IsSuccess);
    }

    private async Task<int> CountOfAsync(Guid patternId, CancellationToken ct)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ResourcePatternAssignments.AsNoTracking().CountAsync(assignment => assignment.WorkPatternId == patternId, ct);
    }

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static IRenderedComponent<IComponent> HostDialogs(PageContext page) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
    });

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
    }

    private async Task<Circuit> CircuitAsync(Guid userId)
    {
        var clock = new FakeClock(Now);
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId), services => services.AddSingleton<TimeProvider>(clock));
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime, clock);
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly RuntimeExecutionContext runtime;
        private readonly TimeProvider clock;

        public Circuit(ServiceHost host, RuntimeExecutionContext runtime, TimeProvider clock)
        {
            this.host = host;
            this.runtime = runtime;
            this.clock = clock;
        }

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(this.clock);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<WorkPatternService>());
            page.Services.AddSingleton(this.host.Get<PatternAssignmentService>());
            page.Services.AddSingleton(this.host.Get<IPatternAssignmentGuard>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
