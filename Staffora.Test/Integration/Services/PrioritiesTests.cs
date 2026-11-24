using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Scheduling;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class PrioritiesTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateOnly Wednesday = Today.AddDays(2);
    private static readonly DateOnly Thursday = Today.AddDays(3);
    private static readonly DateOnly Friday = Today.AddDays(4);

    private readonly RealDatabase database;

    public PrioritiesTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_handled_sick_leave_stays_in_the_dock_while_its_assignment_is_unsettled_an_approved_leave_without_impact_leaves_it_and_both_still_block()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Priorities Absence", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        var record = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(13, 0), new TimeOnly(14, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        Guid sick, leave;
        await using (var own = scenario.HostOf(this.database, ada))
        {
            sick = Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, Wednesday, Wednesday, Ct));
            leave = Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, Thursday, Thursday, Ct));
        }

        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);

        var board = await PrioritiesAsync(coordinator);
        var reported = Single(board, PriorityItemKind.AbsenceImpact);
        Assert.Equal(("Ada Example", PriorityTarget.AbsenceImpact, sick, (Guid?)sick, "1 assignment to settle"), (reported.Title, reported.Target, reported.TargetId, reported.AbsenceId, reported.Meta));
        Assert.Contains("Lead, Algebra", reported.Text, StringComparison.Ordinal);
        Assert.Equal("Wed 11 13:00", reported.When);
        Assert.Equal((PriorityTarget.Job, job, "Algebra, 1 at risk"), JobOf(Single(board, PriorityItemKind.AtRisk)));
        var toApprove = Single(board, PriorityItemKind.AbsenceToApprove);
        Assert.Equal((leave, "Thu 12"), (toApprove.TargetId, toApprove.When));
        Assert.Equal((2, 1), (board.CountOf(PriorityTab.Absence), board.CountOf(PriorityTab.Slots)));

        Assert.Equal([PriorityItemKind.AtRisk, PriorityItemKind.AbsenceImpact, PriorityItemKind.AbsenceToApprove], board.Items.Select(item => item.Kind));

        var thursday = await PrioritiesAsync(coordinator, new PriorityQuery(Day: Thursday));
        Assert.Equal([PriorityItemKind.AbsenceToApprove], thursday.Items.Select(item => item.Kind));

        Made(await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), sick, ResourceAbsenceWorkflowAttribute.HandleSickStep, Ct));
        Made(await AbsenceScenario.StepAsync(coordinator.Get<ResourceAbsenceService>(), leave, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct));
        board = await PrioritiesAsync(coordinator);
        Assert.DoesNotContain(board.Items, item => item.Kind is PriorityItemKind.AbsenceToApprove);
        Assert.Equal(sick, Single(board, PriorityItemKind.AbsenceImpact).TargetId);

        Assert.Equal((PriorityTarget.Job, job, "Algebra, 1 at risk"), JobOf(Single(board, PriorityItemKind.AtRisk)));
        var staff = Made(await coordinator.Get<CoordinationService>().ReadStaffMembersAsync(Today, new BoardFilter(), Ct));
        var days = Assert.Single(staff.StaffMembers, row => row.ResourceId == record).Days;
        Assert.Equal(("Sick leave, handled", ResourceAbsenceState.Approved, true), Words(Assert.Single(days.Single(day => day.Date == Wednesday).Absences)));
        Assert.Equal(("Planned leave, approved", ResourceAbsenceState.Approved, true), Words(Assert.Single(days.Single(day => day.Date == Thursday).Absences)));
        var week = Made(await coordinator.Get<IStaffAvailability>().ReadAsync([record], Wednesday, Thursday, Ct)).StaffMembers[record];
        Assert.Contains((Guid?)sick, week.Blocks.Select(block => block.SourceId));
        Assert.Contains((Guid?)leave, week.Blocks.Select(block => block.SourceId));
    }

    [Fact]
    public async Task A_staff_question_on_an_absence_waits_for_an_approver_only_and_a_coordinator_without_the_approval_right_sees_no_absence_to_decide()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Priorities Questions", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        var morgan = await scenario.StaffMembers.AddPersonAsync(this.database, "Morgan", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, morgan, Ct, RolePermission.ViewCoordination);

        Guid asked, plain;
        await using (var own = scenario.HostOf(this.database, ada))
        {
            var absences = own.Get<ResourceAbsenceService>();
            asked = Made(await AbsenceScenario.ReportAsync(absences, ada, AbsenceKind.PlannedLeave, Friday, Friday, Ct));
            Made(await AbsenceScenario.StepAsync(absences, asked, ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, Ct, comment: "Can I take the Monday too?"));
            plain = Made(await AbsenceScenario.ReportAsync(absences, ada, AbsenceKind.PlannedLeave, Thursday, Thursday, Ct));
        }

        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var board = await PrioritiesAsync(coordinator);
            var question = Single(board, PriorityItemKind.StaffQuestions);
            Assert.Equal(("Ada Example", PriorityTarget.Absence, asked), (question.Title, question.Target, question.TargetId));
            Assert.Equal([plain], board.Items.Where(item => item.Kind == PriorityItemKind.AbsenceToApprove).Select(item => item.TargetId));
            Assert.Equal((1, 1), (board.CountOf(PriorityTab.Questions), board.CountOf(PriorityTab.Absence)));
        }

        await using (var viewer = scenario.HostOf(this.database, morgan))
        {
            var board = await PrioritiesAsync(viewer);
            Assert.DoesNotContain(board.Items, item => item.Kind is PriorityItemKind.StaffQuestions or PriorityItemKind.AbsenceToApprove or PriorityItemKind.AbsenceImpact);
        }

        await using (var own = scenario.HostOf(this.database, ada))
        {
            Assert.False((await own.Get<CoordinationService>().ReadAsync(Ct)).IsSuccess);
        }
    }

    [Fact]
    public async Task A_job_is_one_row_for_each_kind_its_slots_are_with_what_waits_and_opens_the_jobs_dialog()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Priorities Per Job", Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead", "Assistant", "Tutor"], AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(13, 0), new TimeOnly(14, 0)));
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);

        var board = await PrioritiesAsync(coordinator);
        var open = Single(board, PriorityItemKind.ToRequestOrAssign);
        Assert.Equal(("Brightpath Learning", PriorityTarget.Job, job, "Algebra, 3 of 3 open", "Wed 11 13:00", "Uncovered"), (open.Title, open.Target, open.TargetId, open.Text, open.When, open.Label));
        Assert.Equal(1, board.CountOf(PriorityTab.Slots));

        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[2], bo, Ct));
        board = await PrioritiesAsync(coordinator);
        Assert.Equal((PriorityTarget.Job, job, "Algebra, 2 of 3 open"), JobOf(Single(board, PriorityItemKind.ToRequestOrAssign)));
        Assert.Equal((PriorityTarget.Job, job, "Algebra, 1 asked, no answer"), JobOf(Single(board, PriorityItemKind.UnansweredRequests)));
        Assert.Equal(2, board.CountOf(PriorityTab.Slots));
        Assert.Equal(2, board.Items.Select(item => item.Key).Distinct().Count());
    }

    [Fact]
    public async Task The_dock_is_one_list_of_every_kind_from_the_most_urgent_to_the_least_which_the_tabs_filter_and_a_page_cuts()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Priorities Order", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today.AddDays(-7), Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var (algebra, algebraSlots) = await scenario.StartedJobAsync(this.database, "Algebra", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(13, 0), new TimeOnly(14, 0)));
        var (physics, _) = await scenario.StartedJobAsync(this.database, "Physics", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(14, 0), new TimeOnly(15, 0)));
        var (chemistry, chemistrySlots) = await scenario.StartedJobAsync(this.database, "Chemistry", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebraSlots[0], ada, Ct));
        var asked = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, chemistrySlots[0], bo, Ct));
        Guid sick, leave;
        await using (var own = scenario.HostOf(this.database, ada))
        {
            sick = Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, Wednesday, Wednesday, Ct));
            leave = Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, Thursday, Thursday, Ct));
        }

        Made(await scenario.StepAsync(this.database, bo, asked, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Is it the lab?"));

        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        var board = await PrioritiesAsync(coordinator);

        Assert.Equal(
            [
                (PriorityItemKind.StaffQuestions, "Question", chemistry),
                (PriorityItemKind.ToRequestOrAssign, "Uncovered", physics),
                (PriorityItemKind.AtRisk, "At risk", algebra),
                (PriorityItemKind.AbsenceImpact, "Absent", sick),
                (PriorityItemKind.AbsenceToApprove, "Absence", leave),
                (PriorityItemKind.UnansweredRequests, "Unanswered", chemistry),
            ],
            board.Items.Select(item => (item.Kind, item.Label, item.TargetId)));
        Assert.Equal(sick, Single(board, PriorityItemKind.AbsenceImpact).AbsenceId);
        Assert.Equal((6, 3, 1, 2, 0), (board.Total, board.CountOf(PriorityTab.Slots), board.CountOf(PriorityTab.Questions), board.CountOf(PriorityTab.Absence), board.CountOf(PriorityTab.Orders)));
        Assert.False(board.HasMore);

        var slotsTab = await PrioritiesAsync(coordinator, new PriorityQuery(PriorityTab.Slots));
        Assert.Equal([PriorityItemKind.ToRequestOrAssign, PriorityItemKind.AtRisk, PriorityItemKind.UnansweredRequests], slotsTab.Items.Select(item => item.Kind));
        Assert.Equal(6, slotsTab.Total);

        var page = await PrioritiesAsync(coordinator, new PriorityQuery(Take: 2));
        Assert.Equal([PriorityItemKind.StaffQuestions, PriorityItemKind.ToRequestOrAssign], page.Items.Select(item => item.Kind));
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task Another_tenants_absences_and_slots_never_show_and_a_snapshot_of_another_tenant_is_refused()
    {
        var first = await AssignmentScenario.CreateAsync(this.database, "Priorities First", Ct);
        var second = await AssignmentScenario.CreateAsync(this.database, "Priorities Second", Ct);
        var bo = await second.Absence.AddWorkingStaffAsync(this.database, "Bo", Today.AddDays(-7), Ct);
        var (_, slots) = await second.StartedJobAsync(this.database, "Physics", Today.AddDays(1), Today.AddDays(60), Ct, ["Lead"], AssignmentScenario.Occurrence(Weekday.Wednesday, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        Made(await second.AssignAsync(this.database, second.Coordinator, slots[0], bo, Ct));
        await using (var own = second.HostOf(this.database, bo))
        {
            Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), bo, AbsenceKind.SickLeave, Wednesday, Wednesday, Ct));
            Made(await AbsenceScenario.ReportAsync(own.Get<ResourceAbsenceService>(), bo, AbsenceKind.PlannedLeave, Thursday, Thursday, Ct));
        }

        await using var coordinator = first.HostOf(this.database, first.Coordinator);
        Assert.Empty((await PrioritiesAsync(coordinator)).Items);

        await using var other = second.HostOf(this.database, second.Coordinator);
        var foreign = Made(await other.Get<CoordinationService>().ReadAsync(Ct));
        Assert.Equal(CoordinationService.NotThisTenant, (await coordinator.Get<CoordinationService>().ReadPrioritiesAsync(foreign, new PriorityQuery(), Ct)).Error);
        Assert.Equal(3, (await PrioritiesAsync(other)).Total);
    }

    private static async Task<PriorityBoard> PrioritiesAsync(ServiceHost host, PriorityQuery? query = null)
    {
        var coordination = host.Get<CoordinationService>();
        var snapshot = Made(await coordination.ReadAsync(Ct));
        return Made(await coordination.ReadPrioritiesAsync(snapshot, query ?? new PriorityQuery(), Ct));
    }

    private static PriorityItem Single(PriorityBoard board, PriorityItemKind kind) => Assert.Single(board.Items, item => item.Kind == kind);

    private static (string Text, ResourceAbsenceState State, bool Blocks) Words(BoardAbsence absence) => (absence.Text, absence.State, absence.Blocks);

    private static (PriorityTarget Target, Guid Job, string Text) JobOf(PriorityItem item) => (item.Target, item.TargetId, item.Text);

    private static T Made<T>(Result<T> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value!;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);
}
