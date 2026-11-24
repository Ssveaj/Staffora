using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Messaging;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class RequestRoundReviewTests : IClassFixture<RealDatabase>
{
    private const int Repeats = 3;

    private static readonly TimeSpan WaitForWaiters = TimeSpan.FromSeconds(20);

    private readonly RealDatabase database;

    public RequestRoundReviewTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_coordinators_assign_racing_a_first_to_accept_yes_for_the_last_slot_leaves_one_holder_per_slot()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Review Assign Race", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (boRecord, cyRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct));
        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var (job, first) = await GroupAsync(scenario, $"Assign race {repeat}", repeat, count: 2);
            Made(await scenario.AssignAsync(this.database, scenario.Coordinator, first, ada, Ct));
            var round = await SendAsync(scenario, first, [boRecord], firstToAccept: true);
            var boRequest = (await RequestsOfAsync(round)).Single().Id;

            await using var boHost = scenario.HostOf(this.database, bo);
            await using var coordinatorHost = scenario.HostOf(this.database, scenario.Coordinator);
            var boService = boHost.Get<SlotAssignmentService>();
            var boUpdater = (await boService.LoadSlotAssignmentUpdaterAsync(boRequest, Ct)).Value!;
            var assigning = coordinatorHost.Get<SlotAssignmentService>();

            Result yes;
            Result<Guid> assigned;
            await using (var holder = this.database.CreateSystemContext())
            {
                await using var held = await holder.Database.BeginTransactionAsync(Ct);
                await AppLock.Jobs.TakeAsync(holder, scenario.TenantId, Ct);
                var yesTask = Task.Run(() => boService.UpdateSlotAssignmentAndTransitionAsync(boUpdater, SlotAssignmentWorkflowAttribute.AcceptStep, null, Ct), Ct);
                var assignTask = Task.Run(() => assigning.AssignStaffAsync(first, cyRecord, Ct), Ct);
                await UntilBothWaitAsync(yesTask, assignTask);
                await held.RollbackAsync(Ct);
                (yes, assigned) = (await yesTask, await assignTask);
            }

            Assert.True(yes.IsSuccess ^ assigned.IsSuccess, $"yes: {yes.Error}; assign: {assigned.Error}");
            if (yes.IsSuccess)
            {
                Assert.Contains(SlotAssignmentWorkflowAttribute.GroupOpenReason, assigned.Error, StringComparison.Ordinal);
                Assert.Equal([SlotAssignmentState.Assigned], await AssignmentScenario.StatesAsync(this.database, Ct, boRequest));
            }
            else
            {
                Assert.Equal(SlotAssignmentService.TakenBeforeYourYes, yes.Error);
                Assert.Equal([SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, boRequest));
            }

            await AssertOneHolderPerSlotAsync(job, expectedHolders: 2);
            Assert.Equal(RequestRoundState.Filled, (await RoundAsync(round)).State);
        }
    }

    [Fact]
    public async Task Closing_a_round_racing_a_yes_leaves_no_request_open_and_the_round_closed_never_filled()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Review Close Race", Ct);
        var (bo, cy) = (await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (boRecord, cyRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct));
        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var (job, first) = await GroupAsync(scenario, $"Close race {repeat}", repeat, count: 2);
            var round = await SendAsync(scenario, first, [boRecord, cyRecord], firstToAccept: true);
            var requests = await RequestsOfAsync(round);
            var (boRequest, cyRequest) = (requests.Single(row => row.ResourceId == boRecord).Id, requests.Single(row => row.ResourceId == cyRecord).Id);

            await using var boHost = scenario.HostOf(this.database, bo);
            await using var coordinatorHost = scenario.HostOf(this.database, scenario.Coordinator);
            var boService = boHost.Get<SlotAssignmentService>();
            var rounds = coordinatorHost.Get<RequestRoundService>();
            var boUpdater = (await boService.LoadSlotAssignmentUpdaterAsync(boRequest, Ct)).Value!;
            var roundUpdater = (await rounds.LoadRequestRoundUpdaterAsync(round, Ct)).Value!;

            Result yes;
            Result closed;
            await using (var holder = this.database.CreateSystemContext())
            {
                await using var held = await holder.Database.BeginTransactionAsync(Ct);
                await AppLock.Jobs.TakeAsync(holder, scenario.TenantId, Ct);
                var yesTask = Task.Run(() => boService.UpdateSlotAssignmentAndTransitionAsync(boUpdater, SlotAssignmentWorkflowAttribute.AcceptStep, null, Ct), Ct);
                var closeTask = Task.Run(() => rounds.UpdateRequestRoundAndTransitionAsync(roundUpdater, RequestRoundWorkflowAttribute.CloseStep, null, Ct), Ct);
                await UntilBothWaitAsync(yesTask, closeTask);
                await held.RollbackAsync(Ct);
                (yes, closed) = (await yesTask, await closeTask);
            }

            Assert.True(closed.IsSuccess, closed.Error);
            Assert.Equal(RequestRoundState.Closed, (await RoundAsync(round)).State);
            Assert.Equal([yes.IsSuccess ? SlotAssignmentState.Assigned : SlotAssignmentState.Canceled, SlotAssignmentState.Canceled], await AssignmentScenario.StatesAsync(this.database, Ct, boRequest, cyRequest));
            await AssertOneHolderPerSlotAsync(job, expectedHolders: yes.IsSuccess ? 1 : 0);
            await using var context = this.database.CreateSystemContext();
            Assert.Equal(1, await context.StateHistoryEntries.AsNoTracking().CountAsync(entry => entry.EntityId == round && entry.ToState != (int)RequestRoundState.Open, Ct));
        }
    }

    [Fact]
    public async Task Two_rounds_on_one_group_are_each_filled_once_and_each_sender_told_once()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Review Two Rounds", Ct);
        var (bo, cy) = (await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (boRecord, cyRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct));
        var (job, slot) = await GroupAsync(scenario, "Two rounds", 0, count: 1);
        var firstRound = await SendAsync(scenario, slot, [boRecord], firstToAccept: true);
        var secondRound = await SendAsync(scenario, slot, [cyRecord], firstToAccept: false);
        var boRequest = (await RequestsOfAsync(firstRound)).Single().Id;
        var cyRequest = (await RequestsOfAsync(secondRound)).Single().Id;

        Made(await scenario.StepAsync(this.database, bo, boRequest, SlotAssignmentWorkflowAttribute.AcceptStep, Ct));
        Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, boRequest, cyRequest));
        Assert.Equal((RequestRoundState.Filled, RequestRoundState.Filled), ((await RoundAsync(firstRound)).State, (await RoundAsync(secondRound)).State));
        await AssertOneHolderPerSlotAsync(job, expectedHolders: 1);

        await using var context = this.database.CreateSystemContext();
        var ids = new[] { firstRound, secondRound, boRequest, cyRequest };
        var history = await context.StateHistoryEntries.AsNoTracking().Where(entry => ids.Contains(entry.EntityId)).ToListAsync(Ct);
        var yes = Assert.Single(history, entry => entry.EntityId == boRequest && entry.TransitionKey == SlotAssignmentWorkflowAttribute.AcceptStep);
        foreach (var round in new[] { firstRound, secondRound })
        {
            Assert.Equal(yes.CorrelationId, Assert.Single(history, entry => entry.EntityId == round && entry.TransitionKey == RequestRoundWorkflowAttribute.FilledStep).CorrelationId);
        }

        var notices = await context.OutgoingMessages.AsNoTracking().Where(message => ids.Contains(message.EntityId)).ToListAsync(Ct);
        Assert.Equal(2, notices.Count(message => message.MessageType == MessageTypes.RoundFilled.Key));
        Assert.Equal([cyRequest], notices.Where(message => message.MessageType == MessageTypes.AssignmentNotSelected.Key).Select(message => message.EntityId));
        Assert.DoesNotContain(notices, message => message.MessageType == MessageTypes.AssignmentAccepted.Key);
    }

    [Fact]
    public async Task Canceling_the_open_slot_that_leaves_its_group_full_closes_the_groups_requests_and_fills_its_round()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Review Slot Cancel", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (boRecord, cyRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct));
        var (job, first) = await GroupAsync(scenario, "Slot cancel", 0, count: 2);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, first, ada, Ct));
        var round = await SendAsync(scenario, first, [boRecord, cyRecord], firstToAccept: true);
        var requests = await RequestsOfAsync(round);
        var other = await OtherSlotAsync(job, first);

        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Made(await AssignmentScenario.StepSlotAsync(host.Get<JobSlotService>(), other, JobSlotWorkflowAttribute.CancelStep, Ct));
        }

        Assert.All(await AssignmentScenario.StatesAsync(this.database, Ct, [.. requests.Select(row => row.Id)]), state => Assert.Equal(SlotAssignmentState.NotSelected, state));
        Assert.Equal(RequestRoundState.Filled, (await RoundAsync(round)).State);
        Assert.Equal("Another staff member was picked for Slot cancel", (await scenario.NoticesOfAsync(this.database, bo, Ct))[0].Subject);
        await AssertOneHolderPerSlotAsync(job, expectedHolders: 1);
    }

    [Fact]
    public async Task A_staff_members_own_round_request_holds_no_other_candidate_even_in_memory()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Review Own Request", Ct);
        var (bo, cy) = (await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (boRecord, cyRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct));
        var (_, first) = await GroupAsync(scenario, "Own request", 0, count: 2);
        var round = await SendAsync(scenario, first, [boRecord, cyRecord], firstToAccept: true);
        var boRequest = (await RequestsOfAsync(round)).Single(row => row.ResourceId == boRecord).Id;

        await using var host = scenario.HostOf(this.database, bo);
        var own = await host.Get<SlotAssignmentService>().LoadSlotAssignmentUpdaterAsync(boRequest, Ct);
        Assert.True(own.IsSuccess, own.Error);
        var entity = own.Value!.Entity;
        Assert.NotNull(entity.Round);
        Assert.All(entity.Round!.Requests, request => Assert.Equal(boRequest, request.Id));
        Assert.All(entity.Slot.Job.Slots.SelectMany(slot => slot.Assignments), request => Assert.Equal(boRequest, request.Id));
        Assert.DoesNotContain(cyRecord, entity.Round.Requests.Select(request => request.ResourceId));

        var (_, single) = await GroupAsync(scenario, "Own history", 1, count: 1);
        var second = await SendAsync(scenario, single, [boRecord, cyRecord], firstToAccept: true);
        var requests = await RequestsOfAsync(second);
        Made(await scenario.StepAsync(this.database, bo, requests.Single(row => row.ResourceId == boRecord).Id, SlotAssignmentWorkflowAttribute.AcceptStep, Ct));
        var cyRequest = requests.Single(row => row.ResourceId == cyRecord).Id;
        Assert.Equal([SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, cyRequest));
        await using var cyHost = scenario.HostOf(this.database, cy);
        var history = await cyHost.Get<SlotAssignmentService>().GetSlotAssignmentHistoryAsync(cyRequest, StateHistoryQuery.Newest(10), Ct);
        Assert.Equal((false, StateHistoryService.OwnersOnly), (history.IsSuccess, history.Error));
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseRequestStep, (await AssignmentScenario.HistoryAsync(this.database, cyRequest, Ct))[^1]);
    }

    [Fact]
    public async Task Only_a_coordinator_sends_or_closes_a_round_naming_only_its_own_tenants_staff_and_at_most_thirty()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Review Round Rights", Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var boRecord = await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct);
        var (_, first) = await GroupAsync(scenario, "Round rights", 0, count: 2);
        var other = await AssignmentScenario.CreateAsync(this.database, "Review Round Other", Ct);
        var foreignRecord = await AssignmentScenario.ResourceOfAsync(this.database, await other.AddStaffAsync(this.database, "Olle", Ct), Ct);
        var before = await CountsAsync();

        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var rounds = host.Get<RequestRoundService>();

            Assert.False((await rounds.SendRoundAsync(first, [foreignRecord], firstToAccept: true, Ct)).IsSuccess);
            Assert.False((await rounds.SendRoundAsync(first, [boRecord, foreignRecord], firstToAccept: true, Ct)).IsSuccess);

            Assert.Equal(RequestRoundService.CandidatesOutOfRange, (await rounds.SendRoundAsync(first, [.. Enumerable.Range(0, RequestRoundService.MaxCandidates + 1).Select(_ => Guid.NewGuid())], firstToAccept: true, Ct)).Error);
            Assert.Equal(before, await CountsAsync());
            var once = await rounds.SendRoundAsync(first, [boRecord, boRecord], firstToAccept: true, Ct);
            Assert.True(once.IsSuccess, once.Error);
            Assert.Single(await RequestsOfAsync(once.Value));
        }

        var round = (await RoundsOfAsync(scenario.TenantId)).Single();
        await using (var host = scenario.HostOf(this.database, bo))
        {
            Assert.Equal(RequestRoundService.NoSend, (await host.Get<RequestRoundService>().SendRoundAsync(first, [boRecord], firstToAccept: true, Ct)).Error);
        }

        await using (var coordinatorHost = scenario.HostOf(this.database, scenario.Coordinator))
        await using (var staffHost = scenario.HostOf(this.database, bo))
        {
            var updater = (await coordinatorHost.Get<RequestRoundService>().LoadRequestRoundUpdaterAsync(round, Ct)).Value!;
            Assert.Equal(RequestRoundService.NoSend, (await staffHost.Get<RequestRoundService>().UpdateRequestRoundAndTransitionAsync(updater, RequestRoundWorkflowAttribute.CloseStep, null, Ct)).Error);
        }

        Assert.Equal(RequestRoundState.Open, (await RoundAsync(round)).State);
    }

    [Fact]
    public async Task A_holders_slot_moves_only_by_the_step_into_assigned_never_by_a_plain_save()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Review Slot Move", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, first) = await GroupAsync(scenario, "Slot move", 0, count: 2);
        var held = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, first, ada, Ct));
        var other = await OtherSlotAsync(job, first);

        await using (var context = this.database.CreateContext(scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator)))
        {
            var row = await context.SlotAssignments.AsTracking().SingleAsync(assignment => assignment.Id == held, Ct);
            row.JobSlotId = other;
            var refused = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(Ct));
            Assert.Contains(nameof(SlotAssignment.JobSlotId), refused.Fields);
        }

        Assert.Equal(first, (await AssignmentScenario.StoredAsync(this.database, held, Ct))!.JobSlotId);
    }

    private async Task<(Guid Job, Guid First)> GroupAsync(AssignmentScenario scenario, string name, int week, int count)
    {
        var monday = new DateOnly(2027, 2, 1).AddDays(7 * week);
        var (job, _) = await scenario.StartedJobAsync(this.database, name, monday, monday.AddDays(6), Ct, slots: ["Supervisor"]);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var first = Made(await host.Get<JobSlotService>().CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = count }, JobSlotWorkflowAttribute.AddStep, Ct));
        return (job, first);
    }

    private async Task<Guid> SendAsync(AssignmentScenario scenario, Guid slot, IReadOnlyList<Guid> resources, bool firstToAccept)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        return Made(await host.Get<RequestRoundService>().SendRoundAsync(slot, resources, firstToAccept, Ct));
    }

    private async Task AssertOneHolderPerSlotAsync(Guid job, int expectedHolders)
    {
        await using var context = this.database.CreateSystemContext();
        var holders = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.Slot.JobId == job && assignment.Slot.Name == "Cleaner" && assignment.State == SlotAssignmentState.Assigned)
            .Select(assignment => assignment.JobSlotId)
            .ToListAsync(Ct);
        Assert.Equal(holders.Count, holders.Distinct().Count());
        Assert.Equal(expectedHolders, holders.Count);
    }

    private async Task<Guid> OtherSlotAsync(Guid job, Guid first)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.JobSlots.AsNoTracking().Where(slot => slot.JobId == job && slot.Name == "Cleaner" && slot.Id != first).Select(slot => slot.Id).SingleAsync(Ct);
    }

    private async Task<List<SlotAssignment>> RequestsOfAsync(Guid round)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.RequestRoundId == round).ToListAsync(Ct);
    }

    private async Task<RequestRound> RoundAsync(Guid round)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.RequestRounds.AsNoTracking().SingleAsync(row => row.Id == round, Ct);
    }

    private async Task<List<Guid>> RoundsOfAsync(Guid tenantId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.RequestRounds.AsNoTracking().Where(round => round.TenantId == tenantId).Select(round => round.Id).ToListAsync(Ct);
    }

    private async Task<(int Rounds, int Assignments, int History, int Outbox)> CountsAsync()
    {
        await using var context = this.database.CreateSystemContext();
        return (await context.RequestRounds.CountAsync(Ct), await context.SlotAssignments.CountAsync(Ct), await context.StateHistoryEntries.CountAsync(Ct), await context.OutgoingMessages.CountAsync(Ct));
    }

    private async Task UntilBothWaitAsync(params Task[] racers)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(WaitForWaiters);
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND resource_database_id = DB_ID()";
        while ((int)(await command.ExecuteScalarAsync(deadline.Token))! < 2)
        {
            Assert.DoesNotContain(racers, racer => racer.IsCompleted);
        }
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result made) => Assert.True(made.IsSuccess, made.Error);
}
