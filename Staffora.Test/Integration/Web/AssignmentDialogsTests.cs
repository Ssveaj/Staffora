using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class AssignmentDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public AssignmentDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_coordinator_asks_a_staff_member_from_the_slot_the_staff_member_says_yes_from_their_dialog_and_the_coordinator_assigns_them()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Dialog Assignments", cancellationToken);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", cancellationToken);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), cancellationToken, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var slot = slots[0];

        await using (var circuit = await CircuitAsync(scenario.Coordinator))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => JobSlotDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobSlotService>(), slot));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staff]"));
            rendered.WaitForAssertion(() => Assert.Contains("No staff member asked yet", rendered.Find(".state-entity-dialog").TextContent, StringComparison.Ordinal), Wait);
            Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Request or assign a staff member");
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staffing]"));
            rendered.WaitForAssertion(() => Assert.NotNull(rendered.Find(".state-entity-dialog [data-candidate='Ada Example']")), Wait);
            Assert.Contains("Algebra", rendered.Find(".state-entity-dialog [data-staffing-summary]").TextContent, StringComparison.Ordinal);
            Assert.NotNull(rendered.Find("[data-candidate='Ada Example'] button[data-staff-action='assign']"));
            ClickOnce(rendered, () => rendered.Find("[data-candidate='Ada Example'] button[data-staff-action='request']"));
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog [data-candidate='Ada Example']")), Wait);
            Assert.Single(rendered.FindAll(".state-entity-dialog"));
        }

        var assignment = await RequestedOfAsync(slot, cancellationToken);

        await using (var circuit = await CircuitAsync(scenario.Coordinator))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => JobSlotDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobSlotService>(), slot));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staff]"));
            rendered.WaitForAssertion(() => Assert.Contains("Ada Example", rendered.Find(".state-entity-dialog tr.list-row").TextContent, StringComparison.Ordinal), Wait);
            ClickOnce(rendered, () => rendered.Find(".state-entity-dialog tr.list-row").QuerySelector("button.list-row__open")!);
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<SlotAssignmentDialog>()), Wait);
            WaitForDialog(rendered);
            var shown = rendered.FindAll(".state-entity-dialog").Last();
            Assert.Equal(("Ada Example", "Algebra", "Lead", "Monday 08:00-09:30"), (shown.QuerySelector("[data-staff]")!.TextContent, shown.QuerySelector("[data-job]")!.TextContent, shown.QuerySelector("[data-slot]")!.TextContent, shown.QuerySelector("[data-occurrences]")!.TextContent));
            Assert.Equal([SlotAssignmentWorkflowAttribute.CancelRequestStep], Steps(shown));
            Assert.Null(shown.QuerySelector("[data-status]"));
            Assert.NotNull(shown.QuerySelector("[data-assignment-notice]"));
        }

        await using (var circuit = await CircuitAsync(ada))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => SlotAssignmentDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<SlotAssignmentService>(), assignment));
            WaitForDialog(rendered);
            var own = rendered.FindAll(".state-entity-dialog").Last();
            Assert.Equal(("Your answer is needed", "Algebra", "No service named", "Lead", "None asked for"), (own.QuerySelector("[data-status]")!.TextContent, own.QuerySelector("[data-job]")!.TextContent, own.QuerySelector("[data-service]")!.TextContent, own.QuerySelector("[data-slot]")!.TextContent, own.QuerySelector("[data-qualifications]")!.TextContent));
            Assert.Null(own.QuerySelector("[data-staff]"));
            Assert.Null(own.QuerySelector("[data-assignment-notice]"));
            Assert.Equal([SlotAssignmentWorkflowAttribute.AcceptStep, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, SlotAssignmentWorkflowAttribute.DeclineStep], Steps(own).Order(StringComparer.Ordinal));
            ClickOnce(rendered, () => StepButton(rendered, SlotAssignmentWorkflowAttribute.AcceptStep));
            rendered.WaitForAssertion(() => Assert.Equal("Accepted", rendered.Find(".state-entity-dialog .state-pill__button").GetAttribute("data-state")), Wait);
        }

        await using (var circuit = await CircuitAsync(scenario.Coordinator))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => SlotAssignmentDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<SlotAssignmentService>(), assignment));
            WaitForDialog(rendered);
            Assert.Contains(SlotAssignmentWorkflowAttribute.PickStep, Steps(rendered.FindAll(".state-entity-dialog").Last()));
            ClickOnce(rendered, () => StepButton(rendered, SlotAssignmentWorkflowAttribute.PickStep));
            rendered.WaitForAssertion(() => Assert.Equal("Assigned", rendered.Find(".state-entity-dialog .state-pill__button").GetAttribute("data-state")), Wait);
        }

        Assert.Equal(SlotAssignmentState.Assigned, (await AssignmentScenario.StoredAsync(this.database, assignment, cancellationToken))!.State);
    }

    private async Task<Guid> RequestedOfAsync(Guid slot, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var made = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(context.SlotAssignments.Where(row => row.JobSlotId == slot), cancellationToken);
        Assert.Equal(SlotAssignmentState.Requested, made.State);
        return made.Id;
    }

    private static List<string> Steps(IElement dialog) => [.. dialog.QuerySelectorAll("button[data-transition]").Select(button => button.GetAttribute("data-transition")!)];

    private static IElement StepButton(IRenderedComponent<IComponent> rendered, string key) =>
        rendered.FindAll(".state-entity-dialog").Last().QuerySelector($"button[data-transition='{key}']") ?? throw new InvalidOperationException($"No button for {key}.");

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
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId), AssignmentScenario.UseClock);
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
            page.Services.AddSingleton(AssignmentScenario.Clock);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<JobService>());
            page.Services.AddSingleton(this.host.Get<JobSlotService>());
            page.Services.AddSingleton(this.host.Get<SlotAssignmentService>());
            page.Services.AddSingleton(this.host.Get<RequestRoundService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Jobs.IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Jobs.ISlotAssignmentGuard>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
