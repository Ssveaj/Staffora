using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class AssignmentLifecycleTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public AssignmentLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Staff_members_are_asked_one_accepts_one_declines_with_a_reason_and_picking_the_yes_closes_the_open_request_each_step_told_to_the_other_party()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Request Staffing", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var slot = slots[0];
        var coordinator = scenario.Coordinator;

        var adaAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slot, ada, Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slot, bo, Ct));
        var cyAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slot, cy, Ct));
        Assert.Equal([SlotAssignmentState.Requested, SlotAssignmentState.Requested, SlotAssignmentState.Requested], await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment, cyAssignment));
        var asked = Assert.Single(await scenario.NoticesOfAsync(this.database, ada, Ct));
        Assert.Equal(("You are asked to work Algebra", $"?dlg=assignment-{adaAssignment}"), (asked.Subject, asked.Link));
        Assert.Contains("as Lead, 2027-02-01 to 2027-06-11", asked.Text, StringComparison.Ordinal);
        Assert.Equal($"?dlg=assignment-{cyAssignment}", Assert.Single(await scenario.NoticesOfAsync(this.database, cy, Ct)).Link);
        Assert.Equal(JobCoverage.Uncovered, await SlotCoverageAsync(scenario, job, slot));

        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        var accepted = Assert.Single(await scenario.NoticesOfAsync(this.database, coordinator, Ct));
        Assert.Equal(("Ada Example accepted Algebra", $"?dlg=assignment-{adaAssignment}"), (accepted.Subject, accepted.Link));
        Assert.Equal(JobCoverage.Covered, await SlotCoverageAsync(scenario, job, slot));

        Assert.False((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.DeclineStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "I work elsewhere on Mondays")).IsSuccess);
        Assert.Equal(["I work elsewhere on Mondays"], await AssignmentScenario.StaffThreadAsync(this.database, boAssignment, Ct));
        var declined = (await scenario.NoticesOfAsync(this.database, coordinator, Ct))[0];
        Assert.Equal("Bo Example declined Algebra", declined.Subject);
        Assert.DoesNotContain("Mondays", declined.Text, StringComparison.Ordinal);
        await using (var reader = scenario.HostOf(this.database, coordinator))
        {
            var threads = await reader.Get<StaffConversationService>().GetSlotAssignmentCommentsAsync(boAssignment, Ct);
            Assert.Equal(["I work elsewhere on Mondays"], Assert.Single(threads.Value!, thread => thread.Audience == CommentAudience.Staff).Comments.Select(comment => comment.Text));
        }

        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.Declined, SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment, cyAssignment));
        Assert.Equal([SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentWorkflowAttribute.AcceptStep, SlotAssignmentWorkflowAttribute.PickStep], await AssignmentScenario.HistoryAsync(this.database, adaAssignment, Ct));
        Assert.Equal([SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentWorkflowAttribute.DeclineStep], await AssignmentScenario.HistoryAsync(this.database, boAssignment, Ct));
        Assert.Equal([SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentWorkflowAttribute.CloseRequestStep], await AssignmentScenario.HistoryAsync(this.database, cyAssignment, Ct));
        Assert.Equal("You are assigned to Algebra", (await scenario.NoticesOfAsync(this.database, ada, Ct))[0].Subject);
        var notSelected = (await scenario.NoticesOfAsync(this.database, cy, Ct))[0];
        Assert.Equal(("Another staff member was picked for Algebra", $"?dlg=assignment-{cyAssignment}"), (notSelected.Subject, notSelected.Link));
        Assert.DoesNotContain("Ada", notSelected.Text, StringComparison.Ordinal);
        Assert.Single(await scenario.NoticesOfAsync(this.database, bo, Ct));

        await using var host = scenario.HostOf(this.database, coordinator);
        var row = Assert.Single((await host.Get<JobService>().QueryJobSlotsAsync(job, new ListQuery(), Ct)).Value!.Rows);
        Assert.Equal(("Ada Example (assigned)", JobCoverage.Covered), (row.StaffMembersLabel, row.Coverage));
    }

    [Fact]
    public async Task Assigning_straight_away_closes_the_open_requests_and_a_slot_takes_no_second_holder_nor_a_staff_member_twice()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Holder Staffing", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Geometry", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var (slot, assistant) = (slots[0], slots[1]);
        var coordinator = scenario.Coordinator;

        var adaAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slot, ada, Ct));
        Assert.Contains(SlotAssignmentWorkflowAttribute.OneLiveReason, (await scenario.RequestAsync(this.database, coordinator, slot, ada, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(SlotAssignmentWorkflowAttribute.OneLiveReason, (await scenario.AssignAsync(this.database, coordinator, slot, ada, Ct)).Error, StringComparison.Ordinal);
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slot, bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);

        var cyAssignment = Made(await scenario.AssignAsync(this.database, coordinator, slot, cy, Ct));
        Assert.Equal([SlotAssignmentState.NotSelected, SlotAssignmentState.NotSelected, SlotAssignmentState.Assigned], await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment, cyAssignment));
        Assert.Equal([SlotAssignmentWorkflowAttribute.AssignStep], await AssignmentScenario.HistoryAsync(this.database, cyAssignment, Ct));
        Assert.Equal(SlotAssignmentWorkflowAttribute.NotSelectedStep, (await AssignmentScenario.HistoryAsync(this.database, adaAssignment, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseRequestStep, (await AssignmentScenario.HistoryAsync(this.database, boAssignment, Ct))[^1]);
        Assert.Equal(("You are assigned to Geometry", $"?dlg=assignment-{cyAssignment}"), Subject(Assert.Single(await scenario.NoticesOfAsync(this.database, cy, Ct))));
        Assert.Equal("Another staff member was picked for Geometry", (await scenario.NoticesOfAsync(this.database, ada, Ct))[0].Subject);
        Assert.Equal("Another staff member was picked for Geometry", (await scenario.NoticesOfAsync(this.database, bo, Ct))[0].Subject);

        Assert.Equal(SlotAssignmentWorkflowAttribute.OneHolderReason, (await scenario.AssignAsync(this.database, coordinator, slot, dee, Ct)).Error);
        Assert.Equal(SlotAssignmentWorkflowAttribute.OneHolderReason, (await scenario.RequestAsync(this.database, coordinator, slot, dee, Ct)).Error);
        Assert.False((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal(1, await CountAsync(slot, SlotAssignmentState.Assigned));
        Assert.Equal(3, await CountAsync(slot));

        Made(await scenario.AssignAsync(this.database, coordinator, assistant, dee, Ct));

        Assert.True((await scenario.StepAsync(this.database, coordinator, cyAssignment, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct)).IsSuccess);
        Assert.Equal(("Geometry is canceled for you", $"?dlg=assignment-{cyAssignment}"), Subject((await scenario.NoticesOfAsync(this.database, cy, Ct))[0]));
        Assert.Contains(AvailabilityRules.AssignedReason, (await scenario.AssignAsync(this.database, coordinator, slot, dee, Ct)).Error, StringComparison.Ordinal);
        var boAgain = Made(await scenario.AssignAsync(this.database, coordinator, slot, bo, Ct));
        Assert.Contains(SlotAssignmentWorkflowAttribute.OneHolderReason, (await scenario.RequestAsync(this.database, coordinator, slot, ada, Ct)).Error, StringComparison.Ordinal);
        Assert.True((await scenario.StepAsync(this.database, coordinator, boAgain, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct)).IsSuccess);
        var again = Made(await scenario.RequestAsync(this.database, coordinator, slot, ada, Ct));
        Assert.NotEqual(adaAssignment, again);
        Assert.Equal(SlotAssignmentState.NotSelected, (await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct))!.State);
    }

    [Fact]
    public async Task A_staff_member_takes_back_a_yes_with_a_reason_and_each_cancellation_tells_the_staff_member()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Withdraw Staffing", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Chemistry", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;

        var adaAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.False((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.WithdrawStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.WithdrawStep, Ct, "My schedule changed")).IsSuccess);
        Assert.Equal(SlotAssignmentState.Declined, (await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct))!.State);
        Assert.Equal(["My schedule changed"], await AssignmentScenario.StaffThreadAsync(this.database, adaAssignment, Ct));
        Assert.Equal("Ada Example took back their yes for Chemistry", (await scenario.NoticesOfAsync(this.database, coordinator, Ct))[0].Subject);

        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, coordinator, boAssignment, SlotAssignmentWorkflowAttribute.CancelRequestStep, Ct)).IsSuccess);
        var cyAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], cy, Ct));
        Assert.True((await scenario.StepAsync(this.database, cy, cyAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, coordinator, cyAssignment, SlotAssignmentWorkflowAttribute.CancelAcceptedStep, Ct)).IsSuccess);
        var adaAgain = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAgain, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct)).IsSuccess);
        Assert.Equal([SlotAssignmentState.Canceled, SlotAssignmentState.Canceled, SlotAssignmentState.Canceled], await AssignmentScenario.StatesAsync(this.database, Ct, boAssignment, cyAssignment, adaAgain));
        Assert.Equal([SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentWorkflowAttribute.CancelRequestStep], await AssignmentScenario.HistoryAsync(this.database, boAssignment, Ct));
        Assert.Equal([SlotAssignmentWorkflowAttribute.RequestStep, SlotAssignmentWorkflowAttribute.AcceptStep, SlotAssignmentWorkflowAttribute.CancelAcceptedStep], await AssignmentScenario.HistoryAsync(this.database, cyAssignment, Ct));
        Assert.Equal([SlotAssignmentWorkflowAttribute.AssignStep, SlotAssignmentWorkflowAttribute.CancelAssignedStep], await AssignmentScenario.HistoryAsync(this.database, adaAgain, Ct));
        foreach (var (staff, assignment) in new[] { (bo, boAssignment), (cy, cyAssignment), (ada, adaAgain) })
        {
            Assert.Equal(("Chemistry is canceled for you", $"?dlg=assignment-{assignment}"), Subject((await scenario.NoticesOfAsync(this.database, staff, Ct))[0]));
        }

        Assert.False((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.False((await scenario.StepAsync(this.database, coordinator, cyAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
    }

    [Fact]
    public async Task Canceling_a_slot_cancels_its_live_assignments_each_with_its_history_and_a_notice_and_leaves_the_closed_ones()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Slot Cancel Staffing", Ct);
        var staffMembers = new List<Guid>();
        foreach (var name in new[] { "Ada", "Bo", "Cy", "Dee", "Eli" })
        {
            staffMembers.Add(await scenario.AddStaffAsync(this.database, name, Ct));
        }

        var (_, slots) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;

        var requested = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], staffMembers[0], Ct));
        var accepted = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], staffMembers[1], Ct));
        Assert.True((await scenario.StepAsync(this.database, staffMembers[1], accepted, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        var declined = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], staffMembers[2], Ct));
        Assert.True((await scenario.StepAsync(this.database, staffMembers[2], declined, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "Not this period")).IsSuccess);
        var notSelected = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], staffMembers[4], Ct));
        var assigned = Made(await scenario.AssignAsync(this.database, coordinator, slots[1], staffMembers[3], Ct));

        var editor = await scenario.StaffMembers.AddPersonAsync(this.database, "Edda", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, editor, Ct, RolePermission.JobsEdit, RolePermission.JobsView);
        await using (var editing = scenario.HostOf(this.database, editor))
        {
            var refused = await AssignmentScenario.StepSlotAsync(editing.Get<JobSlotService>(), slots[0], JobSlotWorkflowAttribute.CancelStep, Ct);
            Assert.Contains("needs the permission", refused.Error, StringComparison.Ordinal);
        }

        Assert.Equal(JobSlotState.Active, (await StoredSlotAsync(slots[0])).State);

        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var slotService = host.Get<JobSlotService>();
            Assert.True((await AssignmentScenario.StepSlotAsync(slotService, slots[0], JobSlotWorkflowAttribute.CancelStep, Ct)).IsSuccess);
            Assert.True((await AssignmentScenario.StepSlotAsync(slotService, slots[1], JobSlotWorkflowAttribute.CancelStep, Ct)).IsSuccess);
        }

        Assert.Equal(
            [SlotAssignmentState.Canceled, SlotAssignmentState.Canceled, SlotAssignmentState.Declined, SlotAssignmentState.Canceled, SlotAssignmentState.NotSelected],
            await AssignmentScenario.StatesAsync(this.database, Ct, requested, accepted, declined, assigned, notSelected));
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelRequestStep, (await AssignmentScenario.HistoryAsync(this.database, requested, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelAcceptedStep, (await AssignmentScenario.HistoryAsync(this.database, accepted, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelAssignedStep, (await AssignmentScenario.HistoryAsync(this.database, assigned, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.DeclineStep, (await AssignmentScenario.HistoryAsync(this.database, declined, Ct))[^1]);
        await using (var context = this.database.CreateSystemContext())
        {
            var slotEntry = await context.StateHistoryEntries.AsNoTracking().SingleAsync(entry => entry.EntityId == slots[0] && entry.TransitionKey == JobSlotWorkflowAttribute.CancelStep, Ct);
            var cascaded = await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == requested || entry.EntityId == accepted).Where(entry => entry.TransitionKey.StartsWith("assignment-cancel")).ToListAsync(Ct);
            Assert.All(cascaded, entry => Assert.Equal(slotEntry.CorrelationId, entry.CorrelationId));
        }

        foreach (var (staff, assignment) in new[] { (staffMembers[0], requested), (staffMembers[1], accepted), (staffMembers[3], assigned) })
        {
            Assert.Equal(("Biology is canceled for you", $"?dlg=assignment-{assignment}"), Subject((await scenario.NoticesOfAsync(this.database, staff, Ct))[0]));
        }

        Assert.DoesNotContain(await scenario.NoticesOfAsync(this.database, staffMembers[2], Ct), notice => notice.Subject.Contains("canceled", StringComparison.Ordinal));
        Assert.DoesNotContain(await scenario.NoticesOfAsync(this.database, staffMembers[4], Ct), notice => notice.Subject.Contains("canceled", StringComparison.Ordinal));

        Assert.Contains(SlotAssignmentWorkflowAttribute.SlotActiveReason, (await scenario.RequestAsync(this.database, coordinator, slots[0], staffMembers[0], Ct)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_members_are_asked_assigned_and_answer_while_the_job_is_confirmed_and_in_progress_and_not_before_coordination()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Running Staffing", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var coordinator = scenario.Coordinator;

        Guid registered;
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var jobs = host.Get<JobService>();
            var created = await jobs.CreateJobAsync(new Job { Name = "Draft job", CustomerId = scenario.Customer, OrganizationUnitId = scenario.StaffMembers.RootId, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd, TimeSlots = [AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))] }, JobWorkflowAttribute.RegisterStep, Ct);
            registered = (await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = created.Value, Name = "Lead" }, JobSlotWorkflowAttribute.AddStep, Ct)).Value;
        }

        Assert.Contains(SlotAssignmentWorkflowAttribute.StaffingOpenReason, (await scenario.RequestAsync(this.database, coordinator, registered, ada, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(SlotAssignmentWorkflowAttribute.StaffingOpenReason, (await scenario.AssignAsync(this.database, coordinator, registered, ada, Ct)).Error, StringComparison.Ordinal);

        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Running job", today.AddDays(-7), today.AddDays(90), Ct, ["Lead", "Assistant"]);
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            Assert.True((await AssignmentScenario.StepJobAsync(host.Get<JobService>(), job, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        }

        var adaAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.Assigned, (await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct))!.State);

        await using (var clock = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(new FakeClock(DateTimeOffset.UtcNow));
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        }))
        {
            await clock.Get<JobClock>().RunOnceAsync(Ct);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            Assert.Equal(JobState.InProgress, (await context.Jobs.AsNoTracking().SingleAsync(row => row.Id == job, Ct)).State);
        }

        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "Fully booked")).IsSuccess);
        var cyAssignment = Made(await scenario.AssignAsync(this.database, coordinator, slots[1], cy, Ct));
        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct)).IsSuccess);
        var boMain = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boMain, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, coordinator, boMain, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal(
            [SlotAssignmentState.Canceled, SlotAssignmentState.Declined, SlotAssignmentState.Assigned, SlotAssignmentState.Assigned],
            await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment, cyAssignment, boMain));
    }

    [Fact]
    public async Task Only_the_staff_member_asked_answers_by_every_path_and_a_platform_owner_impersonating_them_answers_as_them()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Owner Staffing", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var adaAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));

        Assert.False((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var past = await host.Get<WorkflowService>().TransitionEntityAsync<SlotAssignmentState, SlotAssignment>(adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, SlotAssignmentWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Contains("needs the permission", past.Error, StringComparison.Ordinal);
        }

        Assert.Equal(SlotAssignmentService.NotFound, (await scenario.StepAsync(this.database, bo, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).Error);
        await using (var host = scenario.HostOf(this.database, bo))
        {
            var workflows = host.Get<WorkflowService>();
            var yes = await workflows.TransitionEntityAsync<SlotAssignmentState, SlotAssignment>(adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, SlotAssignmentWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Contains(SlotAssignmentWorkflowAttribute.OwnReason, yes.Error, StringComparison.Ordinal);
            var no = await workflows.TransitionEntityAsync<SlotAssignmentState, SlotAssignment>(adaAssignment, SlotAssignmentWorkflowAttribute.DeclineStep, SlotAssignmentWorkflowQueries.WithWorkflowGraph, "Not mine", Ct);
            Assert.Contains(SlotAssignmentWorkflowAttribute.OwnReason, no.Error, StringComparison.Ordinal);
            var footer = await workflows.GetAvailableTransitionsAsync<SlotAssignmentState, SlotAssignment>(adaAssignment, SlotAssignmentWorkflowQueries.WithWorkflowGraph, Ct);
            Assert.Contains(SlotAssignmentWorkflowAttribute.OwnReason, footer.Value!.Single(step => step.Transition.Key == SlotAssignmentWorkflowAttribute.AcceptStep).BlockingReasons);
        }

        Assert.False((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.CancelRequestStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct))!.State);

        var impersonating = scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(ada, scenario.TenantId);
        await using (var host = ServiceHost.Create(this.database, impersonating))
        {
            Assert.True((await AssignmentScenario.StepAsync(host.Get<SlotAssignmentService>(), adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            var entry = await context.StateHistoryEntries.AsNoTracking().SingleAsync(row => row.EntityId == adaAssignment && row.TransitionKey == SlotAssignmentWorkflowAttribute.AcceptStep, Ct);
            Assert.Equal((ada, (Guid?)scenario.StaffMembers.Onboarded.Platform.OwnerId), (entry.ActorId, entry.ImpersonatedById));
        }

        await using (var console = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime()))
        {
            var assignments = console.Get<SlotAssignmentService>();
            Assert.Equal(JobService.ChangeInOwnTenantOnly, (await assignments.RequestStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), Ct)).Error);
            var reached = await console.Get<WorkflowService>().TransitionEntityAsync<SlotAssignmentState, SlotAssignment>(adaAssignment, SlotAssignmentWorkflowAttribute.PickStep, SlotAssignmentWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Contains(JobWorkflowAttribute.OwnTenantReason, reached.Error, StringComparison.Ordinal);
        }

        Assert.Equal(SlotAssignmentState.Accepted, (await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct))!.State);
    }

    [Fact]
    public async Task A_staff_member_reads_their_own_assignments_only_and_nothing_of_another_staff_members()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Own Staffing", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "History", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var adaAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "Bo's private reason")).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);

        await using var host = scenario.HostOf(this.database, ada);
        var assignments = host.Get<SlotAssignmentService>();

        Assert.Equal(SlotAssignmentService.NotFound, (await assignments.LoadSlotAssignmentUpdaterAsync(boAssignment, Ct)).Error);
        Assert.Equal(SlotAssignmentService.NotFound, (await assignments.GetSlotAssignmentHistoryAsync(boAssignment, StateHistoryQuery.Newest(10), Ct)).Error);
        Assert.Equal(SlotAssignmentService.NotFound, (await assignments.Conversations.GetSlotAssignmentCommentsAsync(boAssignment, Ct)).Error);
        Assert.Equal(SlotAssignmentService.NotFound, (await assignments.Conversations.AddSlotAssignmentCommentAsync(boAssignment, CommentAudience.Staff, "Hello", Ct)).Error);

        Assert.Equal(SlotAssignmentService.NoView, (await assignments.QuerySlotAssignmentsAsync(slots[0], new ListQuery(), Ct)).Error);
        Assert.Equal(JobService.NoView, (await host.Get<JobService>().QueryJobSlotsAsync(job, new ListQuery(), Ct)).Error);
        Assert.Equal(StaffingService.NoView, (await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery(), Ct)).Error);

        var own = await assignments.LoadSlotAssignmentUpdaterAsync(adaAssignment, Ct);
        Assert.True(own.IsSuccess, own.Error);
        Assert.Equal([adaAssignment], own.Value!.Entity.Slot.Assignments.Select(other => other.Id));
        Assert.Equal(StateHistoryService.OwnersOnly, (await assignments.GetSlotAssignmentHistoryAsync(adaAssignment, StateHistoryQuery.Newest(10), Ct)).Error);
        Assert.True((await assignments.Conversations.AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Which room?", Ct)).IsSuccess);
        Assert.Equal(["Which room?"], await AssignmentScenario.StaffThreadAsync(this.database, adaAssignment, Ct));

        var notices = await scenario.NoticesOfAsync(this.database, ada, Ct);
        Assert.DoesNotContain(notices, notice => notice.Link == $"?dlg=assignment-{boAssignment}" || notice.Text.Contains("Bo", StringComparison.Ordinal));

        await using (var coordinating = scenario.HostOf(this.database, coordinator))
        {
            Assert.Equal(StateHistoryService.OwnersOnly, (await coordinating.Get<SlotAssignmentService>().GetSlotAssignmentHistoryAsync(adaAssignment, StateHistoryQuery.Newest(10), Ct)).Error);
        }

        await using var owning = scenario.HostOf(this.database, scenario.StaffMembers.Onboarded.OwnerId);
        var full = await owning.Get<SlotAssignmentService>().GetSlotAssignmentHistoryAsync(adaAssignment, StateHistoryQuery.Newest(10), Ct);
        Assert.True(full.IsSuccess, full.Error);
        Assert.Contains(full.Value!.Steps, step => step.Notices is { Count: > 0 });
    }

    [Fact]
    public async Task Another_tenant_reaches_no_assignment_slot_or_staff_member_and_a_staff_member_out_of_service_is_not_staffed()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Isolated Staffing", Ct);
        var other = await AssignmentScenario.CreateAsync(this.database, "Other Staffing", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var otherStaff = await other.AddStaffAsync(this.database, "Olle", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Music", PeriodStart, PeriodEnd, Ct);
        var (_, otherSlots) = await other.StartedJobAsync(this.database, "Art", PeriodStart, PeriodEnd, Ct);
        var adaAssignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        await using (var host = other.HostOf(this.database, other.Coordinator))
        {
            var assignments = host.Get<SlotAssignmentService>();
            Assert.Equal("The JobSlot was not found.", (await assignments.RequestStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, otherStaff, Ct), Ct)).Error);
            Assert.Equal("The Resource was not found.", (await assignments.AssignStaffAsync(otherSlots[0], await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), Ct)).Error);
            Assert.Equal(SlotAssignmentService.NotFound, (await assignments.LoadSlotAssignmentUpdaterAsync(adaAssignment, Ct)).Error);
            Assert.Empty((await assignments.QuerySlotAssignmentsAsync(slots[0], new ListQuery(), Ct)).Value!.Rows);
            var reached = await host.Get<WorkflowService>().TransitionEntityAsync<SlotAssignmentState, SlotAssignment>(adaAssignment, SlotAssignmentWorkflowAttribute.CancelRequestStep, SlotAssignmentWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.False(reached.IsSuccess);
        }

        Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct))!.State);
        await using (var context = this.database.CreateSystemContext())
        {
            Assert.Equal(0, await context.SlotAssignments.CountAsync(assignment => assignment.TenantId == other.TenantId, Ct));
        }

        var (bo, cy) = (await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            foreach (var staff in new[] { bo, cy })
            {
                var holding = await StaffScenario.HoldingIdAsync(this.database, staff, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct);
                Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
            }
        }

        Assert.Contains(SlotAssignmentWorkflowAttribute.StaffInServiceReason, (await scenario.StepAsync(this.database, scenario.Coordinator, boAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(SlotAssignmentWorkflowAttribute.StaffInServiceReason, (await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], cy, Ct)).Error, StringComparison.Ordinal);
        Assert.Equal(SlotAssignmentService.NoView, (await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.WithdrawStep, Ct, "Leaving")).Error);
        await using var choosing = scenario.HostOf(this.database, scenario.Coordinator);
        var candidates = await choosing.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery(), Ct);
        Assert.True(candidates.IsSuccess, candidates.Error);
        Assert.DoesNotContain(candidates.Value!.Rows, staff => staff.Name is "Bo Example" or "Cy Example" or "Ada Example");
    }

    [Fact]
    public async Task The_filtered_unique_indexes_refuse_a_second_holder_and_a_second_live_assignment_written_past_the_workflow()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Index Staffing", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Drama", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;

        var adaOld = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, coordinator, adaOld, SlotAssignmentWorkflowAttribute.CancelRequestStep, Ct)).IsSuccess);
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));
        Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));

        await using var context = this.database.CreateSystemContext();
        var holder = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlRawAsync("UPDATE SlotAssignments SET State = {0} WHERE Id = {1}", [(int)SlotAssignmentState.Assigned, boAssignment], Ct));
        Assert.Contains("IX_SlotAssignments_OneHolder", holder.Message, StringComparison.Ordinal);
        var live = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlRawAsync("UPDATE SlotAssignments SET State = {0} WHERE Id = {1}", [(int)SlotAssignmentState.Requested, adaOld], Ct));
        Assert.Contains("IX_SlotAssignments_OneLivePerStaff", live.Message, StringComparison.Ordinal);
        Assert.Equal([SlotAssignmentState.NotSelected, SlotAssignmentState.Canceled], await AssignmentScenario.StatesAsync(this.database, Ct, boAssignment, adaOld));
    }

    private async Task<JobCoverage> SlotCoverageAsync(AssignmentScenario scenario, Guid job, Guid slot)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        return (await host.Get<JobService>().QueryJobSlotsAsync(job, new ListQuery(), Ct)).Value!.Rows.Single(row => row.Id == slot).Coverage;
    }

    private async Task<JobSlot> StoredSlotAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.JobSlots.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<int> CountAsync(Guid slot, SlotAssignmentState? state = null)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.SlotAssignments.CountAsync(assignment => assignment.JobSlotId == slot && (state == null || assignment.State == state), Ct);
    }

    private static Guid Made(Staffora.Core.Models.Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static (string Subject, string? Link) Subject((string Subject, string Text, string? Link) notice) => (notice.Subject, notice.Link);
}
