using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Coordination;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed partial class JobClock
{
    internal async Task<(int Rounds, int Requests, int Failed)> ExpireRequestsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = this.scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runtime = ActAsSystemAccount(services.GetRequiredService<IRuntimeExecutionContext>(), tenantId);
        using var logScope = this.log.BeginScope("Job clock in tenant {TenantId}", tenantId);

        var now = this.clock.GetUtcNow().UtcDateTime;
        List<Guid> rounds;
        List<Guid> requests;
        await using (var context = this.dbFactory.CreateDbContext(runtime))
        {
            rounds = await context.RequestRounds.AsNoTracking()
                .Where(round => round.TenantId == tenantId
                    && round.State == RequestRoundState.Open
                    && round.RespondBy != null
                    && round.RespondBy <= now
                    && round.Requests.Any(request => request.State == SlotAssignmentState.Requested || request.State == SlotAssignmentState.StaffQuestion))
                .OrderBy(round => round.RespondBy)
                .ThenBy(round => round.Id)
                .Select(round => round.Id)
                .ToListAsync(cancellationToken);
            requests = await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId
                    && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion)
                    && assignment.RespondBy != null
                    && assignment.RespondBy <= now
                    && (assignment.RequestRoundId == null || assignment.Round!.State != RequestRoundState.Open))
                .OrderBy(assignment => assignment.RespondBy)
                .ThenBy(assignment => assignment.Id)
                .Select(assignment => assignment.Id)
                .ToListAsync(cancellationToken);
        }

        var workflows = services.GetRequiredService<WorkflowService>();
        var stepLocks = services.GetServices<IStepLock>().ToList();
        var (expiredRounds, expiredRequests, failed) = (0, 0, 0);
        foreach (var roundId in rounds)
        {
            switch (await ExpireOneAsync(roundId, "request round", token => ExpireRoundAsync(runtime, workflows, stepLocks, roundId, token), cancellationToken))
            {
                case true:
                    expiredRounds++;
                    break;
                case null:
                    failed++;
                    break;
            }
        }

        foreach (var requestId in requests)
        {
            switch (await ExpireOneAsync(requestId, "request", token => ExpireRequestAsync(runtime, workflows, stepLocks, requestId, token), cancellationToken))
            {
                case true:
                    expiredRequests++;
                    break;
                case null:
                    failed++;
                    break;
            }
        }

        if (expiredRounds + expiredRequests > 0)
        {
            this.log.LogInformation("The job clock expired {Rounds} request rounds and {Requests} requests of tenant {TenantId} whose answer-by time passed.", expiredRounds, expiredRequests, tenantId);
        }

        return (expiredRounds, expiredRequests, failed);
    }

    private async Task<bool?> ExpireOneAsync(Guid id, string kind, Func<CancellationToken, Task<bool>> expire, CancellationToken cancellationToken)
    {
        try
        {
            return await expire(cancellationToken);
        }
        catch (SystemStepRefusedException refused)
        {
            this.log.LogWarning("The job clock left the {Kind} {Id} as it is: {Reason}", kind, id, refused.Message);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "The job clock could not expire the {Kind} {Id}; it goes on with the others.", kind, id);
            return null;
        }
    }

    private Task<bool> ExpireRoundAsync(IRuntimeExecutionContext runtime, WorkflowService workflows, IReadOnlyList<IStepLock> stepLocks, Guid roundId, CancellationToken cancellationToken) =>
        ExpireInTransactionAsync(runtime, async (context, afterCommit, token) =>
        {
            await TakeLocksAsync<RequestRound>(context, stepLocks, roundId, token);
            var round = await context.RequestRounds.WithWorkflowGraph().AsTracking().FirstOrDefaultAsync(row => row.Id == roundId, token);
            if (round is not { State: RequestRoundState.Open } || !SlotAssignmentWorkflowAttribute.IsPast(round.RespondBy, this.clock.GetUtcNow().UtcDateTime)
                || !round.Requests.Any(request => SlotAssignmentWorkflowAttribute.IsAsked(request.State)))
            {
                return false;
            }

            await workflows.RunSystemStepInTransactionAsync<RequestRoundState, RequestRound>(context, round, RequestRoundWorkflowAttribute.ExpireStep, token, afterCommit);
            return true;
        }, cancellationToken);

    private Task<bool> ExpireRequestAsync(IRuntimeExecutionContext runtime, WorkflowService workflows, IReadOnlyList<IStepLock> stepLocks, Guid requestId, CancellationToken cancellationToken) =>
        ExpireInTransactionAsync(runtime, async (context, afterCommit, token) =>
        {
            await TakeLocksAsync<SlotAssignment>(context, stepLocks, requestId, token);
            var request = await context.SlotAssignments.WithWorkflowGraph().AsTracking().FirstOrDefaultAsync(row => row.Id == requestId, token);
            if (request is null || SlotAssignmentWorkflowAttribute.ExpireStepFor(request.State) is not { } step
                || !SlotAssignmentWorkflowAttribute.IsPast(request.RespondBy, this.clock.GetUtcNow().UtcDateTime)
                || request.Round is { State: RequestRoundState.Open })
            {
                return false;
            }

            await workflows.RunSystemStepInTransactionAsync<SlotAssignmentState, SlotAssignment>(context, request, step, token, afterCommit);
            return true;
        }, cancellationToken);

    private static async Task TakeLocksAsync<TEntity>(ApplicationDbContext context, IReadOnlyList<IStepLock> stepLocks, Guid id, CancellationToken cancellationToken)
    {
        foreach (var stepLock in stepLocks.Where(stepLock => stepLock.EntityType.IsAssignableFrom(typeof(TEntity))))
        {
            await stepLock.TakeAsync(context, id, cancellationToken);
        }
    }

    private async Task<bool> ExpireInTransactionAsync(IRuntimeExecutionContext runtime, Func<ApplicationDbContext, AfterCommitWork, CancellationToken, Task<bool>> expire, CancellationToken cancellationToken)
    {
        var afterCommit = new AfterCommitWork();
        TenantChangeCollector touched;
        await using (var context = this.dbFactory.CreateDbContext(runtime))
        {
            touched = TenantChangeCollector.Attach(context, runtime.TenantId);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (!await expire(context, afterCommit, cancellationToken))
            {
                return false;
            }

            await touched.NameAffectedAsync(context, this.log, cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }

        try
        {
            touched.PublishTo(this.changes);
        }
        catch (Exception exception)
        {
            this.log.LogError(exception, "Publishing the job clock's expiry failed after its commit; the step stands.");
        }

        await afterCommit.RunAsync(this.log);
        return true;
    }
}
