using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Staffing;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class StaffingDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public StaffingDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_job_is_fully_staffed_from_the_staffing_dialog_and_an_absent_staff_member_is_offered_no_step()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Dialog Staffing", cancellationToken);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", cancellationToken), await scenario.AddStaffAsync(this.database, "Bo", cancellationToken), await scenario.AddStaffAsync(this.database, "Cy", cancellationToken));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), cancellationToken, ["Lead", "Assistant"]);
        await using (var reporting = scenario.HostOf(this.database, cy))
        {
            Assert.True((await AbsenceScenario.ReportAsync(reporting.Get<ResourceAbsenceService>(), cy, AbsenceKind.SickLeave, new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 8), cancellationToken)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(scenario.Coordinator);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);

        var slotDialog = OpenStaffing(rendered, circuit, page, slots[0]);
        Assert.Contains("Mon 08:00-10:00", slotDialog.QuerySelector("[data-staffing-summary]")!.TextContent, StringComparison.Ordinal);
        var cyRow = Row(rendered, "Cy Example");
        Assert.Equal("true", cyRow.QuerySelector("[data-blocked]")!.GetAttribute("data-blocked"));
        Assert.Contains("On sick leave (reported)", cyRow.QuerySelector("[data-block]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("2027-03-08", cyRow.QuerySelector("[data-block]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cyRow.QuerySelectorAll("button[data-staff-action]").Length);
        Assert.All(cyRow.QuerySelectorAll("button[data-staff-action]"), button => Assert.True(button.HasAttribute("disabled")));
        Assert.Equal(2, Row(rendered, "Ada Example").QuerySelectorAll("button[data-staff-action]:not([disabled])").Length);
        Assert.Single(rendered.FindAll(".state-entity-dialog"));
        AssignInPlace(rendered, "Ada Example");
        rendered.WaitForAssertion(() => Assert.Contains("Filled by Ada Example", rendered.Find(".state-entity-dialog [data-staffing-filled]").TextContent, StringComparison.Ordinal), Wait);
        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog-header__close").Last());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);

        OpenStaffing(rendered, circuit, page, slots[1]);
        rendered.WaitForAssertion(() => Assert.Equal("true", Row(rendered, "Ada Example").QuerySelector("[data-blocked]")?.GetAttribute("data-blocked")), Wait);
        AssignInPlace(rendered, "Bo Example");

        var states = await AssignmentScenario.StatesAsync(this.database, cancellationToken, [.. await AssignmentsOfAsync(slots)]);
        Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.Assigned], states);
        var listed = (await circuit.Get<JobService>().QueryJobsAsync(new ListQuery(), cancellationToken)).Value!.Rows.Single(row => row.Id == job);
        Assert.Equal(JobCoverage.Covered, listed.Coverage);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_staffing_view_shows_each_open_request_with_its_answer_by_time_and_a_request_sent_from_it_answers_by_the_time_chosen()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Dialog Deadline", cancellationToken);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", cancellationToken), await scenario.AddStaffAsync(this.database, "Bo", cancellationToken));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), cancellationToken, ["Lead"]);
        await using var circuit = await CircuitAsync(scenario.Coordinator);
        var never = await circuit.Get<SlotAssignmentService>().RequestStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, bo, cancellationToken), ResponseWindow.None, cancellationToken);
        Assert.True(never.IsSuccess, never.Error);

        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        OpenStaffing(rendered, circuit, page, slots[0]);
        var dialog = rendered.FindAll(".state-entity-dialog").Last();
        Assert.Contains("2 days", dialog.QuerySelector("[data-respond-within]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("No deadline to answer", Asked(rendered, "Bo Example").QuerySelector("[data-answer-by]")!.TextContent.Trim());

        ClickOnce(rendered, () => Row(rendered, "Ada Example").QuerySelector("button[data-staff-action='request']")!);
        rendered.WaitForAssertion(() => Assert.NotNull(rendered.FindAll(".state-entity-dialog").Last().QuerySelector("[data-asked='Ada Example']")), Wait);

        var request = await AssignmentScenario.StoredAsync(this.database, (await AssignmentsOfAsync(slots[0])).Single(id => id != never.Value), cancellationToken);
        var sent = request!.RespondBy!.Value - request.Created;
        Assert.InRange(sent, TimeSpan.FromHours(48) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(48) + TimeSpan.FromMinutes(1));
        var zone = (await circuit.Get<Staffora.Services.Settings.TenantClock>().GetTimeZoneAsync(cancellationToken)).Value!;
        var expected = ResponseWindow.AnswerByText(Staffora.Services.Settings.TenantClock.ToLocal(request.RespondBy.Value, zone));
        Assert.StartsWith("Answer by ", expected, StringComparison.Ordinal);
        rendered.WaitForAssertion(() => Assert.Equal(expected, Asked(rendered, "Ada Example").QuerySelector("[data-answer-by]")!.TextContent.Trim()), Wait);
    }

    private static IElement Asked(IRenderedComponent<IComponent> rendered, string name) =>
        rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("[data-asked]").Single(row => row.GetAttribute("data-asked") == name);

    private async Task<Guid[]> AssignmentsOfAsync(Guid slot)
    {
        await using var context = this.database.CreateSystemContext();
        return [.. context.SlotAssignments.Where(row => row.JobSlotId == slot).Select(row => row.Id)];
    }

    private static IElement OpenStaffing(IRenderedComponent<IComponent> rendered, Circuit circuit, PageContext page, Guid slot)
    {
        _ = rendered.InvokeAsync(() => JobSlotDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<JobSlotService>(), slot));
        WaitForDialog(rendered);
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staffing]"));
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog [data-candidate]")), Wait);
        return rendered.FindAll(".state-entity-dialog").Last();
    }

    private static void AssignInPlace(IRenderedComponent<IComponent> rendered, string name)
    {
        ClickOnce(rendered, () => Row(rendered, name).QuerySelector("button[data-staff-action='assign']")!);
        rendered.WaitForAssertion(() => Assert.DoesNotContain(rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("[data-candidate]"), row => row.GetAttribute("data-candidate") == name), Wait);
        WaitForDialog(rendered);
    }

    private static IElement Row(IRenderedComponent<IComponent> rendered, string name) =>
        rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("[data-candidate]").Single(row => row.GetAttribute("data-candidate") == name);

    private async Task<Guid[]> AssignmentsOfAsync(Guid[] slots)
    {
        await using var context = this.database.CreateSystemContext();
        return [.. slots.Select(slot => context.SlotAssignments.Where(row => row.JobSlotId == slot).Select(row => row.Id).Single())];
    }

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
            page.Services.AddSingleton(this.host.Get<IJobSlotGuard>());
            page.Services.AddSingleton(this.host.Get<ISlotAssignmentGuard>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
