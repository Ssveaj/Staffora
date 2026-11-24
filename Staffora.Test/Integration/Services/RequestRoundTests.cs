using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Lists;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class RequestRoundTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);
    private static readonly string[] Names = ["Ada", "Bo", "Cy", "Dee", "Eve"];

    private readonly RealDatabase database;

    public RequestRoundTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_round_of_five_fills_a_group_of_three_with_the_first_three_yeses_and_closes_the_other_two_each_step_with_its_history_and_notices()
    {
        var (scenario, staff, first, slots) = await GroupAsync("Round Fill Staffing", count: 3);
        var coordinator = scenario.Coordinator;
        var resources = await ResourcesAsync(staff);

        var round = await SendAsync(scenario, first, resources, firstToAccept: true);
        var requests = await RequestsOfAsync(round);
        Assert.Equal(5, requests.Count);
        Assert.All(requests, request => Assert.Equal((SlotAssignmentState.Requested, first), (request.State, request.JobSlotId)));
        var byStaff = staff.ToDictionary(user => user, user => requests.Single(request => request.ResourceId == resources[staff.IndexOf(user)]).Id);

        foreach (var user in staff.Take(3))
        {
            var yes = await scenario.StepAsync(this.database, user, byStaff[user], SlotAssignmentWorkflowAttribute.AcceptStep, Ct);
            Assert.True(yes.IsSuccess, yes.Error);
        }

        var stored = await RequestsOfAsync(round);
        var taken = stored.Where(request => request.State == SlotAssignmentState.Assigned).ToList();
        Assert.Equal(3, taken.Count);
        Assert.Equal(slots.Order(), taken.Select(request => request.JobSlotId).Order());
        Assert.Equal([SlotAssignmentState.NotSelected, SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, byStaff[staff[3]], byStaff[staff[4]]));
        Assert.Equal(RequestRoundState.Filled, (await RoundAsync(round)).State);

        Assert.Equal(SlotAssignmentService.TakenBeforeYourYes, (await scenario.StepAsync(this.database, staff[3], byStaff[staff[3]], SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).Error);

        await using (var context = this.database.CreateSystemContext())
        {
            var ids = requests.Select(request => request.Id).Append(round).ToList();
            var history = await context.StateHistoryEntries.AsNoTracking().Where(entry => ids.Contains(entry.EntityId)).ToListAsync(Ct);
            var send = Assert.Single(history, entry => entry.EntityId == round && entry.TransitionKey == RequestRoundWorkflowAttribute.SendStep);
            Assert.Equal(((int)RequestRoundState.Create, (int)RequestRoundState.Open, coordinator), (send.FromState, send.ToState, send.ActorId));
            var asked = history.Where(entry => entry.TransitionKey == SlotAssignmentWorkflowAttribute.RequestStep).ToList();
            Assert.Equal(5, asked.Count);
            Assert.All(asked, entry => Assert.Equal((send.CorrelationId, coordinator, (int)SlotAssignmentState.Requested), (entry.CorrelationId, entry.ActorId, entry.ToState)));

            foreach (var user in staff.Take(3))
            {
                var accept = Assert.Single(history, entry => entry.EntityId == byStaff[user] && entry.TransitionKey == SlotAssignmentWorkflowAttribute.AcceptStep);
                var claim = Assert.Single(history, entry => entry.EntityId == byStaff[user] && entry.TransitionKey == SlotAssignmentWorkflowAttribute.ClaimStep);
                Assert.Equal((user, (int)SlotAssignmentState.Requested, (int)SlotAssignmentState.Accepted), (accept.ActorId, accept.FromState, accept.ToState));
                Assert.Equal((accept.CorrelationId, (int)SlotAssignmentState.Accepted, (int)SlotAssignmentState.Assigned), (claim.CorrelationId, claim.FromState, claim.ToState));
            }

            var third = Assert.Single(history, entry => entry.EntityId == byStaff[staff[2]] && entry.TransitionKey == SlotAssignmentWorkflowAttribute.AcceptStep);
            var closed = history.Where(entry => entry.TransitionKey == SlotAssignmentWorkflowAttribute.CloseRequestStep).ToList();
            Assert.Equal(new[] { byStaff[staff[3]], byStaff[staff[4]] }.Order(), closed.Select(entry => entry.EntityId).Order());
            Assert.All(closed, entry => Assert.Equal(third.CorrelationId, entry.CorrelationId));
            var filled = Assert.Single(history, entry => entry.EntityId == round && entry.TransitionKey == RequestRoundWorkflowAttribute.FilledStep);
            Assert.Equal((third.CorrelationId, (int)RequestRoundState.Open, (int)RequestRoundState.Filled), (filled.CorrelationId, filled.FromState, filled.ToState));

            var notices = await context.OutgoingMessages.AsNoTracking().Where(message => ids.Contains(message.EntityId)).ToListAsync(Ct);
            Assert.Equal(
                [
                    (MessageTypes.AssignmentAssigned.Key, 3), (MessageTypes.AssignmentNotSelected.Key, 2), (MessageTypes.AssignmentRequestedInRound.Key, 5),
                    (MessageTypes.AssignmentTaken.Key, 3), (MessageTypes.RoundFilled.Key, 1),
                ],
                notices.GroupBy(message => message.MessageType).Select(group => (group.Key, group.Count())).OrderBy(group => group.Key, StringComparer.Ordinal));
            Assert.DoesNotContain(notices, message => message.MessageType == MessageTypes.AssignmentAccepted.Key || message.MessageType == MessageTypes.AssignmentRequested.Key);
            var resourceOf = requests.ToDictionary(request => request.Id, request => staff[resources.IndexOf(request.ResourceId)]);
            foreach (var notice in notices.Where(message => message.MessageType is "assignment-requested-in-round" or "assignment-assigned" or "assignment-not-selected"))
            {
                Assert.Equal([resourceOf[notice.EntityId]], notice.RecipientUserIds);
                var others = Names.Where(name => scenarioNameOf(resourceOf[notice.EntityId]) != name);
                Assert.DoesNotContain(others, name => notice.Text.Contains(name, StringComparison.Ordinal));
            }

            Assert.All(notices.Where(message => message.MessageType == MessageTypes.AssignmentRequestedInRound.Key), message => Assert.Contains(RequestRoundWorkflowAttribute.FirstToAcceptRule, message.Text, StringComparison.Ordinal));
            Assert.All(notices.Where(message => message.MessageType is "assignment-taken" or "round-filled"), message => Assert.Contains(coordinator, message.RecipientUserIds));
            Assert.Contains("Ada Example, Bo Example, Cy Example", Assert.Single(notices, message => message.MessageType == MessageTypes.RoundFilled.Key).Text, StringComparison.Ordinal);
        }

        Assert.Equal(("Another staff member was picked for Office cleaning", $"?dlg=assignment-{byStaff[staff[3]]}"), Subject((await scenario.NoticesOfAsync(this.database, staff[3], Ct))[0]));
        Assert.Equal("Round for Cleaner on Office cleaning is filled", (await scenario.NoticesOfAsync(this.database, coordinator, Ct))[0].Subject);

        string scenarioNameOf(Guid user) => Names[staff.IndexOf(user)];
    }

    [Fact]
    public async Task With_first_to_accept_off_the_yeses_wait_for_the_coordinators_pick_and_the_picks_fill_the_group()
    {
        var (scenario, staff, first, _) = await GroupAsync("Round Pick Staffing", count: 2);
        var resources = await ResourcesAsync(staff.Take(3));
        var round = await SendAsync(scenario, first, resources, firstToAccept: false);
        var requests = await RequestsOfAsync(round);
        var ada = requests.Single(request => request.ResourceId == resources[0]).Id;
        var bo = requests.Single(request => request.ResourceId == resources[1]).Id;
        var cy = requests.Single(request => request.ResourceId == resources[2]).Id;

        Assert.True((await scenario.StepAsync(this.database, staff[0], ada, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, staff[1], bo, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.Equal([SlotAssignmentState.Accepted, SlotAssignmentState.Accepted, SlotAssignmentState.Requested], await AssignmentScenario.StatesAsync(this.database, Ct, ada, bo, cy));
        Assert.Contains("Ada Example accepted Office cleaning", (await scenario.NoticesOfAsync(this.database, scenario.Coordinator, Ct)).Select(notice => notice.Subject));

        Assert.True((await scenario.StepAsync(this.database, scenario.Coordinator, ada, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal(RequestRoundState.Open, (await RoundAsync(round)).State);
        Assert.True((await scenario.StepAsync(this.database, scenario.Coordinator, bo, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.Assigned, SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, ada, bo, cy));
        Assert.NotEqual((await AssignmentScenario.StoredAsync(this.database, ada, Ct))!.JobSlotId, (await AssignmentScenario.StoredAsync(this.database, bo, Ct))!.JobSlotId);
        Assert.Equal(RequestRoundState.Filled, (await RoundAsync(round)).State);
    }

    [Fact]
    public async Task Closing_a_round_cancels_its_unanswered_requests_with_notices_and_leaves_its_yeses()
    {
        var (scenario, staff, first, _) = await GroupAsync("Round Close Staffing", count: 3);
        var resources = await ResourcesAsync(staff.Take(3));
        var round = await SendAsync(scenario, first, resources, firstToAccept: false);
        var requests = await RequestsOfAsync(round);
        var ada = requests.Single(request => request.ResourceId == resources[0]).Id;
        Assert.True((await scenario.StepAsync(this.database, staff[0], ada, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);

        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var rounds = host.Get<RequestRoundService>();
            var updater = (await rounds.LoadRequestRoundUpdaterAsync(round, Ct)).Value!;
            var closed = await rounds.UpdateRequestRoundAndTransitionAsync(updater, RequestRoundWorkflowAttribute.CloseStep, null, Ct);
            Assert.True(closed.IsSuccess, closed.Error);
        }

        Assert.Equal(RequestRoundState.Closed, (await RoundAsync(round)).State);
        var after = await RequestsOfAsync(round);
        Assert.Equal(SlotAssignmentState.Accepted, after.Single(request => request.Id == ada).State);
        Assert.All(after.Where(request => request.Id != ada), request => Assert.Equal(SlotAssignmentState.Canceled, request.State));
        foreach (var user in staff.Skip(1).Take(2))
        {
            Assert.Equal("Office cleaning is canceled for you", (await scenario.NoticesOfAsync(this.database, user, Ct))[0].Subject);
        }
    }

    [Fact]
    public async Task A_staff_member_who_cannot_be_asked_refuses_the_whole_round_another_tenants_slot_reads_as_not_found_and_staff_read_no_round()
    {
        var (scenario, staff, first, _) = await GroupAsync("Round Refused Staffing", count: 2);
        var resources = await ResourcesAsync(staff.Take(3));

        var (_, warehouse) = await scenario.StartedJobAsync(this.database, "Warehouse", PeriodStart, PeriodEnd, Ct, slots: ["Cleaner"]);
        Assert.True((await scenario.AssignAsync(this.database, scenario.Coordinator, warehouse[0], staff[4], Ct)).IsSuccess);
        var eve = (await ResourcesAsync([staff[4]]))[0];
        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var refused = await host.Get<RequestRoundService>().SendRoundAsync(first, [.. resources, eve], firstToAccept: true, Ct);
            Assert.False(refused.IsSuccess);
            Assert.StartsWith("Eve Example cannot be asked: ", refused.Error, StringComparison.Ordinal);
            Assert.EndsWith("Nothing was sent.", refused.Error, StringComparison.Ordinal);
            Assert.Equal(RequestRoundService.CandidatesOutOfRange, (await host.Get<RequestRoundService>().SendRoundAsync(first, [], firstToAccept: true, Ct)).Error);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            Assert.False(await context.RequestRounds.AsNoTracking().AnyAsync(round => round.TenantId == scenario.TenantId, Ct));
            Assert.False(await context.SlotAssignments.AsNoTracking().AnyAsync(assignment => assignment.JobSlotId == first, Ct));
        }

        var other = await AssignmentScenario.CreateAsync(this.database, "Round Other Staffing", Ct);
        var otherStaff = await other.AddStaffAsync(this.database, "Fay", Ct);
        await using (var host = other.HostOf(this.database, other.Coordinator))
        {
            var foreign = await host.Get<RequestRoundService>().SendRoundAsync(first, [(await ResourcesAsync([otherStaff]))[0]], firstToAccept: true, Ct);
            Assert.False(foreign.IsSuccess);
            Assert.Contains("not found", foreign.Error, StringComparison.OrdinalIgnoreCase);
        }

        var round = await SendAsync(scenario, first, resources, firstToAccept: true);
        await using (var host = scenario.HostOf(this.database, staff[0]))
        {
            var rounds = host.Get<RequestRoundService>();
            Assert.Equal(RequestRoundService.NoView, (await rounds.QueryGroupRoundsAsync(first, new ListQuery(), Ct)).Error);
            Assert.Equal(RequestRoundService.NoView, (await rounds.LoadRequestRoundUpdaterAsync(round, Ct)).Error);
        }

        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var rows = (await host.Get<RequestRoundService>().QueryGroupRoundsAsync(first, new ListQuery(), Ct)).Value!.Rows.ToList();
            Assert.Equal((round, RequestRoundState.Open, "3 asked"), (Assert.Single(rows).Id, rows[0].State, rows[0].CountsLabel));
        }
    }

    private async Task<(AssignmentScenario Scenario, List<Guid> Staff, Guid First, Guid[] Slots)> GroupAsync(string name, int count)
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, name, Ct);
        var staff = new List<Guid>();
        foreach (var first in Names)
        {
            staff.Add(await scenario.AddStaffAsync(this.database, first, Ct));
        }

        var (job, _) = await scenario.StartedJobAsync(this.database, "Office cleaning", PeriodStart, PeriodEnd, Ct, slots: ["Supervisor"]);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var added = await host.Get<JobSlotService>().CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = count }, JobSlotWorkflowAttribute.AddStep, Ct);
        Assert.True(added.IsSuccess, added.Error);
        await using var context = this.database.CreateSystemContext();
        var slots = await context.JobSlots.AsNoTracking().Where(slot => slot.JobId == job && slot.Name == "Cleaner").Select(slot => slot.Id).ToArrayAsync(Ct);
        return (scenario, staff, added.Value, slots);
    }

    private async Task<Guid> SendAsync(AssignmentScenario scenario, Guid slot, IReadOnlyList<Guid> resources, bool firstToAccept)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var sent = await host.Get<RequestRoundService>().SendRoundAsync(slot, resources, firstToAccept, Ct);
        Assert.True(sent.IsSuccess, sent.Error);
        return sent.Value;
    }

    private async Task<List<Guid>> ResourcesAsync(IEnumerable<Guid> users)
    {
        var resources = new List<Guid>();
        foreach (var user in users)
        {
            resources.Add(await AssignmentScenario.ResourceOfAsync(this.database, user, Ct));
        }

        return resources;
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

    private static (string Subject, string? Link) Subject((string Subject, string Text, string? Link) notice) => (notice.Subject, notice.Link);
}
