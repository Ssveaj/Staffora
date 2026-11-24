using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class AssignmentReviewTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public AssignmentReviewTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_staff_member_taken_out_of_service_no_longer_covers_their_slot_and_the_job_reads_as_needing_restaffing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Out Of Service", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;

        var adaAssignment = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.Equal((JobCoverage.Covered, JobCoverage.Covered, JobCoverage.Covered), await CoverageAsync(scenario, job, slots));

        await TakeStaffRoleAsync(scenario, ada, bo);
        Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.Accepted], await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment));
        Assert.Equal((JobCoverage.Uncovered, JobCoverage.Uncovered, JobCoverage.Uncovered), await CoverageAsync(scenario, job, slots));
        await using var host = scenario.HostOf(this.database, coordinator);
        var rows = (await host.Get<JobService>().QueryJobSlotsAsync(job, new ListQuery(), Ct)).Value!.Rows;
        Assert.Equal("Ada Example (assigned, out of service)", rows.Single(row => row.Id == slots[0]).StaffMembersLabel);
        var staff = (await host.Get<JobService>().GetJobEditorOptionsAsync(job, Ct)).Value!.Staff!;
        Assert.Equal(["Ada Example (out of service)"], staff.Single(entry => entry.SlotId == slots[0]).Assigned);
    }

    [Fact]
    public async Task Completing_a_job_closes_every_request_and_yes_nobody_picked_and_no_assignment_or_thread_moves_after()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Completed Staffing", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;
        var adaAssignment = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        var cyAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], cy, Ct));
        Assert.True((await scenario.StepAsync(this.database, cy, cyAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        await using (var confirming = scenario.HostOf(this.database, coordinator))
        {
            Assert.True((await AssignmentScenario.StepJobAsync(confirming.Get<JobService>(), job, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        }

        var noticesBefore = (Bo: (await scenario.NoticesOfAsync(this.database, bo, Ct)).Count, Cy: (await scenario.NoticesOfAsync(this.database, cy, Ct)).Count);

        await using (var clockHost = ClockHost(new FakeClock(DateTimeOffset.Parse("2027-06-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture))))
        {
            await clockHost.Get<JobClock>().RunOnceAsync(Ct);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Equal(JobState.Completed, (await system.Jobs.AsNoTracking().SingleAsync(row => row.Id == job, Ct)).State);
            var closing = await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == boAssignment || entry.EntityId == cyAssignment).OrderBy(entry => entry.Created).ToListAsync(Ct);
            Assert.All(closing.Where(entry => entry.ToState == (int)SlotAssignmentState.NotSelected), entry => Assert.Equal(SystemAccount.IdFor(scenario.TenantId), entry.ActorId));
        }

        Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.NotSelected, SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment, cyAssignment));
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseRequestStep, (await AssignmentScenario.HistoryAsync(this.database, boAssignment, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.NotSelectedStep, (await AssignmentScenario.HistoryAsync(this.database, cyAssignment, Ct))[^1]);
        Assert.Equal(noticesBefore, ((await scenario.NoticesOfAsync(this.database, bo, Ct)).Count, (await scenario.NoticesOfAsync(this.database, cy, Ct)).Count));

        var cancel = await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct);
        Assert.Contains(SlotAssignmentWorkflowAttribute.StaffingOpenReason, cancel.Error, StringComparison.Ordinal);
        Assert.Equal(SlotAssignmentState.Assigned, (await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct))!.State);
        await using var coordinating = scenario.HostOf(this.database, coordinator);
        var assignments = coordinating.Get<SlotAssignmentService>();
        Assert.Equal(StaffConversationService.ThreadClosedWithJob, (await assignments.Conversations.AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Thanks for the period", Ct)).Error);
        Assert.False(Assert.Single((await assignments.Conversations.GetSlotAssignmentCommentsAsync(adaAssignment, Ct)).Value!).CanPost);
        await using var working = scenario.HostOf(this.database, ada);
        Assert.Equal(StaffConversationService.ThreadClosedWithJob, (await working.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Thanks", Ct)).Error);
    }

    [Fact]
    public async Task A_staff_member_posts_no_more_once_their_request_is_closed_while_the_coordinator_still_may()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Closed Thread", Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Chemistry", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));

        await using (var working = scenario.HostOf(this.database, bo))
        {
            var service = working.Get<SlotAssignmentService>();
            Assert.Equal(StaffConversationService.WrittenThroughSteps, (await service.Conversations.AddSlotAssignmentCommentAsync(boAssignment, CommentAudience.Staff, "Which room?", Ct)).Error);
        }

        Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Which room?")).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.DeclineQuestionStep, Ct, "Mondays are taken")).IsSuccess);
        await using (var working = scenario.HostOf(this.database, bo))
        {
            var service = working.Get<SlotAssignmentService>();
            Assert.Equal(StaffConversationService.ThreadClosedForStaff, (await service.Conversations.AddSlotAssignmentCommentAsync(boAssignment, CommentAudience.Staff, "One more thing", Ct)).Error);
            var thread = Assert.Single((await service.Conversations.GetSlotAssignmentCommentsAsync(boAssignment, Ct)).Value!);
            Assert.Equal((false, 2), (thread.CanPost, thread.TotalCount));
        }

        await using var coordinating = scenario.HostOf(this.database, coordinator);
        Assert.True((await coordinating.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(boAssignment, CommentAudience.Staff, "Thank you anyway", Ct)).IsSuccess);
        Assert.Equal(["Which room?", "Mondays are taken", "Thank you anyway"], await AssignmentScenario.StaffThreadAsync(this.database, boAssignment, Ct));
    }

    [Fact]
    public async Task A_staff_members_answer_is_told_only_to_those_who_still_coordinate()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Former Coordinator", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var dana = await scenario.StaffMembers.AddPersonAsync(this.database, "Dana", Ct);
        var roles = scenario.StaffMembers.Onboarded.Roles;
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(dana, scenario.StaffMembers.RootId, roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        var (_, slots) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct);
        var adaAssignment = Made(await scenario.RequestAsync(this.database, dana, slots[0], ada, Ct));

        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var holding = await StaffScenario.HoldingIdAsync(this.database, dana, roles.Coordinator, scenario.StaffMembers.RootId, Ct);
            Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(dana, scenario.StaffMembers.TeamId, roles.Staff, scenario.TenantId)).IsSuccess);
        }

        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.Equal("Ada Example accepted Physics", Assert.Single(await scenario.NoticesOfAsync(this.database, scenario.Coordinator, Ct)).Subject);
        Assert.Empty(await scenario.NoticesOfAsync(this.database, dana, Ct));
    }

    [Fact]
    public async Task A_slot_a_staff_member_is_assigned_to_offers_nobody_to_ask_until_the_assignment_is_canceled()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Held Slot", Ct);
        var (ada, _) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "History", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;
        var adaAssignment = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));

        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var options = (await host.Get<JobService>().Slots.GetJobSlotEditorOptionsAsync(slots[0], Ct)).Value!;
            Assert.Equal((false, SlotAssignmentService.SlotHeld), (options.MayStaff, options.Staffing!.Reason));
        }

        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct)).IsSuccess);
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var options = (await host.Get<JobService>().Slots.GetJobSlotEditorOptionsAsync(slots[0], Ct)).Value!;
            Assert.Equal((true, (string?)null), (options.MayStaff, options.Staffing!.Reason));
            var candidates = (await host.Get<JobService>().Slots.Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery(), Ct)).Value!.Rows.ToList();
            Assert.Contains(candidates, staff => staff.Name == "Bo Example");
            Assert.Equal(SlotAssignmentState.Canceled, Assert.Single(candidates, staff => staff.Name == "Ada Example").Earlier);
        }
    }

    [Fact]
    public async Task A_dialogs_load_chosen_as_a_deadlock_victim_is_read_once_more_and_a_second_deadlock_is_answered_not_thrown()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Deadlocked Loads", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Geography", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        var runtime = scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator);

        var loads = new (string Table, Func<ServiceHost, Task<bool>> Load)[]
        {
            ("Jobs", async host => (await host.Get<JobService>().LoadJobUpdaterAsync(job, Ct)).IsSuccess),
            ("SlotAssignments", async host => (await host.Get<SlotAssignmentService>().LoadSlotAssignmentUpdaterAsync(assignment, Ct)).IsSuccess),
            ("JobSlots", async host => (await host.Get<JobService>().Slots.LoadJobSlotUpdaterAsync(slots[0], Ct)).IsSuccess),
            ("Customers", async host => (await host.Get<CustomerService>().LoadCustomerUpdaterAsync(scenario.Customer, Ct)).IsSuccess),
        };
        foreach (var (table, load) in loads)
        {
            var once = new DeadlockingReads(table, 1);
            await using var host = ServiceHost.Create(this.database, runtime, interceptors: [once]);
            Assert.True(await load(host), table);
            Assert.Equal(1, once.Thrown);
        }

        await using (var host = ServiceHost.Create(this.database, runtime, interceptors: [new DeadlockingReads("Jobs", 2)]))
        {
            Assert.Equal(JobService.ReadFailed, (await host.Get<JobService>().LoadJobUpdaterAsync(job, Ct)).Error);
        }

        await using (var host = ServiceHost.Create(this.database, runtime, interceptors: [new DeadlockingReads("SlotAssignments", 2)]))
        {
            Assert.Equal(SlotAssignmentService.ReadFailed, (await host.Get<SlotAssignmentService>().LoadSlotAssignmentUpdaterAsync(assignment, Ct)).Error);
        }
    }

    private async Task<(JobCoverage Job, JobCoverage First, JobCoverage Second)> CoverageAsync(AssignmentScenario scenario, Guid job, Guid[] slots)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var listed = (await jobs.QueryJobsAsync(new ListQuery(), Ct)).Value!.Rows.Single(row => row.Id == job);
        var rows = (await jobs.QueryJobSlotsAsync(job, new ListQuery(), Ct)).Value!.Rows;
        return (listed.Coverage, rows.Single(row => row.Id == slots[0]).Coverage, rows.Single(row => row.Id == slots[1]).Coverage);
    }

    private async Task TakeStaffRoleAsync(AssignmentScenario scenario, params Guid[] staffMembers)
    {
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        foreach (var staff in staffMembers)
        {
            var holding = await StaffScenario.HoldingIdAsync(this.database, staff, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.StaffMembers.TeamId, Ct);
            Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
        }
    }

    private ServiceHost ClockHost(FakeClock clock) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        });

    private static Guid Made(Staffora.Core.Models.Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
