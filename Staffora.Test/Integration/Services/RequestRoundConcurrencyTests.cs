using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class RequestRoundConcurrencyTests : IClassFixture<RealDatabase>
{
    private const int Repeats = 4;

    private static readonly TimeSpan WaitForWaiters = TimeSpan.FromSeconds(20);

    private readonly RealDatabase database;

    public RequestRoundConcurrencyTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_yeses_racing_for_the_last_slot_of_a_group_end_with_one_holder_and_the_other_not_selected()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Round Race Staffing", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (boRecord, cyRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct));

        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var monday = new DateOnly(2027, 2, 1).AddDays(7 * repeat);
            var (job, _) = await scenario.StartedJobAsync(this.database, $"Office cleaning {repeat}", monday, monday.AddDays(6), Ct, slots: ["Supervisor"]);
            Guid first;
            Guid round;
            await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
            {
                first = Made(await host.Get<JobSlotService>().CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = 2 }, JobSlotWorkflowAttribute.AddStep, Ct));
                Made(await scenario.AssignAsync(this.database, scenario.Coordinator, first, ada, Ct));
                round = Made(await host.Get<RequestRoundService>().SendRoundAsync(first, [boRecord, cyRecord], firstToAccept: true, Ct));
            }

            var requests = await RequestsOfAsync(round);
            var boRequest = requests.Single(request => request.ResourceId == boRecord).Id;
            var cyRequest = requests.Single(request => request.ResourceId == cyRecord).Id;

            await using var boHost = scenario.HostOf(this.database, bo);
            await using var cyHost = scenario.HostOf(this.database, cy);
            var (boService, cyService) = (boHost.Get<SlotAssignmentService>(), cyHost.Get<SlotAssignmentService>());
            var (boUpdater, cyUpdater) = ((await boService.LoadSlotAssignmentUpdaterAsync(boRequest, Ct)).Value!, (await cyService.LoadSlotAssignmentUpdaterAsync(cyRequest, Ct)).Value!);

            Result[] results;
            await using (var holder = this.database.CreateSystemContext())
            {
                await using var held = await holder.Database.BeginTransactionAsync(Ct);
                await AppLock.Jobs.TakeAsync(holder, scenario.TenantId, Ct);
                var racers = new[]
                {
                    Task.Run(() => boService.UpdateSlotAssignmentAndTransitionAsync(boUpdater, SlotAssignmentWorkflowAttribute.AcceptStep, null, Ct), Ct),
                    Task.Run(() => cyService.UpdateSlotAssignmentAndTransitionAsync(cyUpdater, SlotAssignmentWorkflowAttribute.AcceptStep, null, Ct), Ct),
                };
                await UntilBothWaitAsync(racers);
                await held.RollbackAsync(Ct);
                results = await Task.WhenAll(racers);
            }

            Assert.Single(results, result => result.IsSuccess);
            Assert.Equal(SlotAssignmentService.TakenBeforeYourYes, Assert.Single(results, result => !result.IsSuccess).Error);
            var states = await AssignmentScenario.StatesAsync(this.database, Ct, boRequest, cyRequest);
            Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.NotSelected], states.Order());
            await using var context = this.database.CreateSystemContext();
            var slots = await context.JobSlots.AsNoTracking().Where(slot => slot.JobId == job && slot.Name == "Cleaner").Select(slot => slot.Id).ToListAsync(Ct);
            var holders = await context.SlotAssignments.AsNoTracking().Where(assignment => slots.Contains(assignment.JobSlotId) && assignment.State == SlotAssignmentState.Assigned).Select(assignment => assignment.JobSlotId).ToListAsync(Ct);
            Assert.Equal(slots.Order(), holders.Order());
            Assert.Equal(RequestRoundState.Filled, await context.RequestRounds.AsNoTracking().Where(row => row.Id == round).Select(row => row.State).SingleAsync(Ct));
        }
    }

    private async Task UntilBothWaitAsync(Task<Result>[] racers)
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

    private async Task<List<SlotAssignment>> RequestsOfAsync(Guid round)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.RequestRoundId == round).ToListAsync(Ct);
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
