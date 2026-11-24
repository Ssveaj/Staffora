using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class AbsenceImpactTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateOnly Wednesday = Today.AddDays(2);
    private static readonly DateOnly Thursday = Today.AddDays(3);

    private readonly RealDatabase database;

    public AbsenceImpactTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sick_leave_over_an_assigned_job_is_in_its_impact_and_the_priorities_until_the_staff_member_is_taken_off_and_told_and_the_slot_is_open_again_until_a_new_assignment_holds_it()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Impact Sick", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        var bo = await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", Today.AddDays(-7), Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(13, 0), new TimeOnly(14, 0)));
        var held = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var absences = coordinator.Get<ResourceAbsenceService>();

        var sick = Made(await AbsenceScenario.RegisterAsync(absences, ada, AbsenceKind.SickLeave, Wednesday, Wednesday, Ct));

        var impact = Made(await absences.ReadAbsenceImpactAsync(sick, Ct));
        Assert.Equal(("Ada Example", true, 1), (impact.Staff, impact.Blocks, impact.Unsettled));
        var row = Assert.Single(impact.Rows);
        Assert.Equal((held, job, slots[0], "Algebra", "Lead", "Brightpath Learning", SlotAssignmentState.Assigned, false), (row.AssignmentId, row.JobId, row.SlotId, row.Job, row.Slot, row.Customer, row.State, row.Settled));
        Assert.Equal(["2026-11-11 13:00-14:00"], row.Occurrences.Select(occurrence => occurrence.Text));

        var board = await PrioritiesAsync(coordinator);
        var waiting = Single(board, PriorityItemKind.AbsenceImpact);
        Assert.Equal(("Ada Example", PriorityTarget.AbsenceImpact, sick, "Sick leave on 2026-11-11, Lead, Algebra", "1 assignment to settle", "Wed 11 13:00"), (waiting.Title, waiting.Target, waiting.TargetId, waiting.Text, waiting.Meta, waiting.When));
        Assert.Equal("Algebra, 1 at risk", Single(board, PriorityItemKind.AtRisk).Text);

        Assert.False((await AssignmentScenario.StepAsync(coordinator.Get<SlotAssignmentService>(), held, SlotAssignmentWorkflowAttribute.TakeOffAcceptedStep, Ct)).IsSuccess);
        Made(await AssignmentScenario.StepAsync(coordinator.Get<SlotAssignmentService>(), held, SlotAssignmentWorkflowAttribute.TakeOffStep, Ct, comment: "Get well soon."));

        Assert.Equal([SlotAssignmentState.Canceled], await AssignmentScenario.StatesAsync(this.database, Ct, held));
        Assert.Equal([SlotAssignmentWorkflowAttribute.AssignStep, SlotAssignmentWorkflowAttribute.TakeOffStep], await AssignmentScenario.HistoryAsync(this.database, held, Ct));
        var told = Assert.Single(await scenario.NoticesOfAsync(this.database, ada, Ct), notice => notice.Subject == "You are taken off Algebra");
        Assert.Equal("Hello Ada, because of your sick leave on 2026-11-11 you no longer work Algebra as Lead. The coordinator asks someone else to work it.\n\nGet well soon.", told.Text);
        Assert.Equal($"?dlg=assignment-{held}", told.Link);
        Assert.Contains("Get well soon.", await AssignmentScenario.StaffThreadAsync(this.database, held, Ct));

        impact = Made(await absences.ReadAbsenceImpactAsync(sick, Ct));
        row = Assert.Single(impact.Rows);
        Assert.Equal(("Taken off", true, true, 0), (row.StateText, row.Settled, row.OpenAgain, impact.Unsettled));
        board = await PrioritiesAsync(coordinator);
        Assert.DoesNotContain(board.Items, item => item.Kind is PriorityItemKind.AbsenceImpact or PriorityItemKind.AtRisk);
        var open = Single(board, PriorityItemKind.ToRequestOrAssign);
        Assert.Equal((job, "Algebra, 1 of 1 open, open again", true), (open.TargetId, open.Text, open.IsOpenAgain));
        Assert.Equal((1, "Open again"), await OpenAgainInStaffingAsync(coordinator, job, slots[0]));

        var asked = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], bo, Ct));
        board = await PrioritiesAsync(coordinator);
        Assert.Equal(("Algebra, 1 asked, no answer, open again", true), (Single(board, PriorityItemKind.UnansweredRequests).Text, Single(board, PriorityItemKind.UnansweredRequests).IsOpenAgain));
        Assert.Equal((1, "Open again"), await OpenAgainInStaffingAsync(coordinator, job, slots[0]));

        Made(await scenario.StepAsync(this.database, bo, asked, SlotAssignmentWorkflowAttribute.AcceptStep, Ct));
        Assert.Equal((1, "Said yes, not assigned yet"), await OpenAgainInStaffingAsync(coordinator, job, slots[0]));
        Made(await scenario.StepAsync(this.database, scenario.Coordinator, asked, SlotAssignmentWorkflowAttribute.PickStep, Ct));

        Assert.Equal((0, "Filled"), await OpenAgainInStaffingAsync(coordinator, job, slots[0]));
        board = await PrioritiesAsync(coordinator);
        Assert.DoesNotContain(board.Items, item => item.IsOpenAgain || item.Kind == PriorityItemKind.AbsenceImpact);
        row = Assert.Single(Made(await absences.ReadAbsenceImpactAsync(sick, Ct)).Rows);
        Assert.Equal(("Taken off", false), (row.StateText, row.OpenAgain));
    }

    [Fact]
    public async Task A_leave_waiting_for_approval_shows_what_it_would_block_but_waits_in_the_priorities_and_takes_nobody_off_until_it_is_approved()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Impact Leave", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Physics", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Thursday, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        var asked = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        Guid leave;
        await using (var own = scenario.HostOf(this.database, ada))
        {
            leave = Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, Thursday, Thursday, Ct));
        }

        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var absences = coordinator.Get<ResourceAbsenceService>();
        var impact = Made(await absences.ReadAbsenceImpactAsync(leave, Ct));
        Assert.Equal((false, SlotAssignmentState.Requested), (impact.Blocks, Assert.Single(impact.Rows).State));
        Assert.DoesNotContain((await PrioritiesAsync(coordinator)).Items, item => item.Kind == PriorityItemKind.AbsenceImpact);
        var refused = await AssignmentScenario.StepAsync(coordinator.Get<SlotAssignmentService>(), asked, SlotAssignmentWorkflowAttribute.TakeOffRequestStep, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains(SlotAssignmentWorkflowAttribute.AwayReason, refused.Error, StringComparison.Ordinal);

        Made(await AbsenceScenario.StepAsync(absences, leave, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct));
        Assert.True(Made(await absences.ReadAbsenceImpactAsync(leave, Ct)).Blocks);
        Assert.Equal(leave, Single(await PrioritiesAsync(coordinator), PriorityItemKind.AbsenceImpact).TargetId);
        Made(await AssignmentScenario.StepAsync(coordinator.Get<SlotAssignmentService>(), asked, SlotAssignmentWorkflowAttribute.TakeOffRequestStep, Ct));
        Assert.Contains(await scenario.NoticesOfAsync(this.database, ada, Ct), notice => notice.Text.StartsWith("Hello Ada, because of your planned leave on 2026-11-12 you no longer work Physics as Lead.", StringComparison.Ordinal));
        Assert.DoesNotContain((await PrioritiesAsync(coordinator)).Items, item => item.Kind == PriorityItemKind.AbsenceImpact);
    }

    [Fact]
    public async Task Only_a_coordinator_of_the_absences_own_tenant_reads_what_it_affects()
    {
        var first = await AssignmentScenario.CreateAsync(this.database, "Impact First", Ct);
        var second = await AssignmentScenario.CreateAsync(this.database, "Impact Second", Ct);
        var bo = await second.Absence.AddWorkingStaffAsync(this.database, "Bo", Today.AddDays(-7), Ct);
        var (_, slots) = await second.StartedJobAsync(this.database, "Chemistry", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        Made(await second.AssignAsync(this.database, second.Coordinator, slots[0], bo, Ct));
        Guid sick;
        await using (var own = second.HostOf(this.database, bo))
        {
            sick = Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), bo, AbsenceKind.SickLeave, Wednesday, Wednesday, Ct));
            Assert.Equal(ResourceAbsenceService.NoImpactView, (await own.Get<ResourceAbsenceService>().ReadAbsenceImpactAsync(sick, Ct)).Error);
        }

        await using (var stranger = first.HostOf(this.database, first.Coordinator))
        {
            Assert.Equal(ResourceAbsenceService.NotFound, (await stranger.Get<ResourceAbsenceService>().ReadAbsenceImpactAsync(sick, Ct)).Error);
            Assert.DoesNotContain((await PrioritiesAsync(stranger)).Items, item => item.Kind == PriorityItemKind.AbsenceImpact);
        }

        await using var coordinator = second.HostOf(this.database, second.Coordinator);
        Assert.Single(Made(await coordinator.Get<ResourceAbsenceService>().ReadAbsenceImpactAsync(sick, Ct)).Rows);
    }

    private static async Task<(int OpenAgain, string Staffed)> OpenAgainInStaffingAsync(ServiceHost host, Guid job, Guid slot)
    {
        var entry = Assert.Single(Made(await host.Get<JobService>().GetJobEditorOptionsAsync(job, Ct)).Staff!);
        var summary = Made(await host.Get<JobSlotService>().ReadSlotStaffingAsync(slot, Ct)).Summary;
        Assert.Equal(entry.OpenAgain, summary.OpenAgain);
        return (summary.OpenAgain, summary.StaffedLabel);
    }

    private static async Task<PriorityBoard> PrioritiesAsync(ServiceHost host)
    {
        var coordination = host.Get<CoordinationService>();
        var snapshot = Made(await coordination.ReadAsync(Ct));
        return Made(await coordination.ReadPrioritiesAsync(snapshot, new PriorityQuery(), Ct));
    }

    private static PriorityItem Single(PriorityBoard board, PriorityItemKind kind) => Assert.Single(board.Items, item => item.Kind == kind);

    private static T Made<T>(Result<T> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value!;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);
}
