using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Settings;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class RespondByDeadlineTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public RespondByDeadlineTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_job_clock_expires_a_request_and_a_rounds_open_requests_together_once_their_answer_by_time_passes_and_tells_the_coordinator()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Deadline Staffing", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Office cleaning", PeriodStart, PeriodEnd, Ct, ["Lead", "Cleaner"]);
        var coordinator = scenario.Coordinator;

        var sentAt = AssignmentScenario.Clock.GetUtcNow();
        Guid single;
        Guid round;
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            single = Made(await host.Get<SlotAssignmentService>().RequestStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), ResponseWindow.OfHours(4), Ct));
            round = Made(await host.Get<RequestRoundService>().SendRoundAsync(slots[1], [.. await ResourcesAsync(bo, cy, dee)], firstToAccept: false, Ct));
        }

        var requests = await RequestsOfAsync(round);
        var (boRequest, cyRequest, deeRequest) = (await RequestOfAsync(requests, bo), await RequestOfAsync(requests, cy), await RequestOfAsync(requests, dee));
        var stored = (await AssignmentScenario.StoredAsync(this.database, single, Ct))!;
        Assert.InRange(stored.RespondBy!.Value - sentAt.UtcDateTime, TimeSpan.FromHours(4), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(5));
        var roundBy = (await RoundAsync(round)).RespondBy!.Value;
        Assert.InRange(roundBy - sentAt.UtcDateTime, TimeSpan.FromHours(48), TimeSpan.FromHours(48) + TimeSpan.FromMinutes(5));
        Assert.All(requests, request => Assert.Equal(roundBy, request.RespondBy));

        Made(await scenario.StepAsync(this.database, cy, cyRequest, SlotAssignmentWorkflowAttribute.AcceptStep, Ct));
        Made(await scenario.StepAsync(this.database, dee, deeRequest, SlotAssignmentWorkflowAttribute.AskCoordinatorStep, Ct, "Do I bring my own cloths?"));
        var noticesBefore = await NoticeCountsAsync(scenario, ada, bo, dee);

        var clock = new FakeClock(sentAt + TimeSpan.FromHours(4) - TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await PassAsync(clock)).Expired);
        Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, single, Ct))!.State);

        clock.Now = sentAt + TimeSpan.FromHours(5);
        Assert.True((await PassAsync(clock)).Expired >= 1);
        Assert.Equal(SlotAssignmentState.Expired, (await AssignmentScenario.StoredAsync(this.database, single, Ct))!.State);
        Assert.Equal(SlotAssignmentWorkflowAttribute.ExpireStep, (await AssignmentScenario.HistoryAsync(this.database, single, Ct))[^1]);
        Assert.Equal([SlotAssignmentState.Requested, SlotAssignmentState.Accepted, SlotAssignmentState.StaffQuestion], await AssignmentScenario.StatesAsync(this.database, Ct, boRequest, cyRequest, deeRequest));
        Assert.Equal(RequestRoundState.Open, (await RoundAsync(round)).State);
        Assert.Equal(["Ada Example did not answer for Office cleaning"], await ExpiryNoticesAsync(scenario, coordinator));

        clock.Now = sentAt + TimeSpan.FromHours(49);
        await PassAsync(clock);
        Assert.Equal(RequestRoundState.Expired, (await RoundAsync(round)).State);
        Assert.Equal([SlotAssignmentState.Expired, SlotAssignmentState.Accepted, SlotAssignmentState.Expired], await AssignmentScenario.StatesAsync(this.database, Ct, boRequest, cyRequest, deeRequest));
        Assert.Equal(SlotAssignmentWorkflowAttribute.ExpireStep, (await AssignmentScenario.HistoryAsync(this.database, boRequest, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.ExpireQuestionStep, (await AssignmentScenario.HistoryAsync(this.database, deeRequest, Ct))[^1]);

        await using (var context = this.database.CreateSystemContext())
        {
            var ids = new[] { single, round, boRequest, deeRequest };
            var steps = await context.StateHistoryEntries.AsNoTracking().Where(entry => ids.Contains(entry.EntityId) && (entry.TransitionKey == SlotAssignmentWorkflowAttribute.ExpireStep || entry.TransitionKey == SlotAssignmentWorkflowAttribute.ExpireQuestionStep || entry.TransitionKey == RequestRoundWorkflowAttribute.ExpireStep)).ToListAsync(Ct);
            Assert.Equal(4, steps.Count);
            Assert.All(steps, step => Assert.Equal(SystemAccount.IdFor(scenario.TenantId), step.ActorId));
            Assert.Single(steps.Where(step => step.EntityId != single).Select(step => step.CorrelationId).Distinct());
        }

        Assert.Equal(["Ada Example did not answer for Office cleaning", "Round for Cleaner on Office cleaning expired"], await ExpiryNoticesAsync(scenario, coordinator));
        var roundNotice = (await scenario.NoticesOfAsync(this.database, coordinator, Ct)).Single(notice => notice.Link == $"?dlg=round-{round}");
        Assert.Equal("The time to answer the round for Cleaner on Office cleaning passed, so the requests nobody answered expired: Bo Example, Dee Example. Ask other staff members for the open slots.", roundNotice.Text);
        Assert.Equal(noticesBefore, await NoticeCountsAsync(scenario, ada, bo, dee));

        Assert.Equal(0, (await PassAsync(clock)).Expired);

        await using (var staff = scenario.HostOf(this.database, ada))
        {
            var own = staff.Get<SlotAssignmentService>();
            Assert.DoesNotContain(Read(await own.QueryOwnAssignmentsAsync(new ListQuery().WithToggle(OwnAssignmentList.ShowClosed, true), Ct)).Rows, row => row.Id == single);
            Assert.DoesNotContain(Read(await own.QueryOwnAssignmentsAsync(new ListQuery(), Ct)).Rows, row => row.Id == single);
            Assert.Empty(Read(await own.ReadOwnWorkAsync(Ct)).Requests);
            Assert.DoesNotContain(OwnAssignmentList.Definition.Filters.Single().Options, option => option.Value == "expired");
        }

        await using (var coordinating = scenario.HostOf(this.database, coordinator))
        {
            var candidates = Read(await coordinating.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery(), Ct)).Rows;
            var again = candidates.Single(row => row.Name == "Ada Example");
            Assert.Equal((SlotAssignmentState.Expired, "No answer before"), (again.Earlier, again.EarlierLabel));

            var snapshot = Read(await coordinating.Get<CoordinationService>().ReadAsync(Ct));
            var lead = snapshot.All.Single(row => row.SlotId == slots[0]);
            Assert.Equal(PriorityKind.Uncovered, lead.Kind);
            Assert.Equal([PriorityRules.NobodyAskedReason, PriorityRules.ExpiredText(1)], lead.Reasons);
            var board = Read(await coordinating.Get<CoordinationService>().ReadPrioritiesAsync(snapshot, new PriorityQuery(), Ct));
            Assert.Contains(board.Items, item => item.Kind == PriorityItemKind.ToRequestOrAssign && item.Text.StartsWith("Office cleaning, ", StringComparison.Ordinal));

            var fresh = Made(await coordinating.Get<SlotAssignmentService>().RequestStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), Ct));
            var refused = await AssignmentScenario.StepAsync(coordinating.Get<SlotAssignmentService>(), fresh, SlotAssignmentWorkflowAttribute.ExpireStep, Ct);
            Assert.False(refused.IsSuccess);
            Assert.Equal(SlotAssignmentState.Requested, (await AssignmentScenario.StoredAsync(this.database, fresh, Ct))!.State);
        }
    }

    [Fact]
    public async Task The_tenant_setting_sets_the_time_to_answer_an_empty_one_sends_requests_that_never_expire_and_the_staff_member_reads_the_deadline()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Deadline Setting", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Window cleaning", PeriodStart, PeriodEnd, Ct, ["Lead", "Helper"]);

        await SaveRespondWithinAsync(scenario, "6");
        var sentAt = AssignmentScenario.Clock.GetUtcNow().UtcDateTime;
        var six = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        Assert.InRange((await AssignmentScenario.StoredAsync(this.database, six, Ct))!.RespondBy!.Value - sentAt, TimeSpan.FromHours(6), TimeSpan.FromHours(6) + TimeSpan.FromMinutes(5));

        await SaveRespondWithinAsync(scenario, string.Empty);
        var never = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[1], bo, Ct));
        Assert.Null((await AssignmentScenario.StoredAsync(this.database, never, Ct))!.RespondBy);

        await using (var staff = scenario.HostOf(this.database, ada))
        {
            var zone = Read(await staff.Get<TenantClock>().GetTimeZoneAsync(Ct));
            var expected = ResponseWindow.AnswerByText(TenantClock.ToLocal((await AssignmentScenario.StoredAsync(this.database, six, Ct))!.RespondBy!.Value, zone));
            Assert.Equal(expected, Read(await staff.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(six, Ct)).AnswerBy);
            Assert.Equal(expected, Read(await staff.Get<SlotAssignmentService>().ReadOwnWorkAsync(Ct)).Requests.Single().AnswerBy);
        }

        await using (var staff = scenario.HostOf(this.database, bo))
        {
            Assert.Null(Read(await staff.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(never, Ct)).AnswerBy);
        }

        await PassAsync(new FakeClock(new DateTimeOffset(2027, 1, 20, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal([SlotAssignmentState.Expired, SlotAssignmentState.Requested], await AssignmentScenario.StatesAsync(this.database, Ct, six, never));
    }

    private async Task SaveRespondWithinAsync(AssignmentScenario scenario, string value)
    {
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime(), AssignmentScenario.UseClock);
        var settings = owner.Get<TenantSettingsService>();
        var loaded = Read(await settings.LoadAsync(Ct));
        var saved = await settings.SaveAsync(TenantSettingDefinitions.RespondWithin.Key, value, loaded.Of(TenantSettingDefinitions.RespondWithin).Version, Ct);
        Assert.True(saved.IsSuccess, saved.Error);
    }

    private async Task<JobClockPass> PassAsync(FakeClock clock)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        });
        return await host.Get<JobClock>().RunOnceAsync(Ct);
    }

    private async Task<List<Guid>> ResourcesAsync(params Guid[] users)
    {
        var resources = new List<Guid>();
        foreach (var user in users)
        {
            resources.Add(await AssignmentScenario.ResourceOfAsync(this.database, user, Ct));
        }

        return resources;
    }

    private async Task<Guid> RequestOfAsync(IEnumerable<SlotAssignment> requests, Guid user)
    {
        var resource = await AssignmentScenario.ResourceOfAsync(this.database, user, Ct);
        return requests.Single(request => request.ResourceId == resource).Id;
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

    private async Task<List<string>> ExpiryNoticesAsync(AssignmentScenario scenario, Guid coordinator) =>
        [.. (await scenario.NoticesOfAsync(this.database, coordinator, Ct)).Select(notice => notice.Subject).Where(subject => subject.Contains("did not answer", StringComparison.Ordinal) || subject.EndsWith(" expired", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private async Task<int[]> NoticeCountsAsync(AssignmentScenario scenario, params Guid[] users)
    {
        var counts = new List<int>();
        foreach (var user in users)
        {
            counts.Add((await scenario.NoticesOfAsync(this.database, user, Ct)).Count);
        }

        return [.. counts];
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result result) => Assert.True(result.IsSuccess, result.Error);

    private static T Read<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }
}
