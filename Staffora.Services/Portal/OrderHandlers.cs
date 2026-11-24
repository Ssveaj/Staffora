using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Integration;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Integration;
using Staffora.Services.Workflow;

namespace Staffora.Services.Portal;

public sealed class JobStateSyncHandler : IIntegrationHandler
{
    private readonly ILogger<JobStateSyncHandler> log;

    public JobStateSyncHandler(ILogger<JobStateSyncHandler> log)
    {
        this.log = log;
    }

    public async Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(context);
        if (OrderSync.ReadJobState(delivery.Payload) is not { } payload)
        {
            this.log.LogWarning("The job message {MessageId} of tenant {SourceTenantId} carries no job state; it changes nothing.", delivery.MessageId, delivery.SourceTenantId);
            return;
        }

        var correlation = delivery.CorrelationId;
        var request = await context.Orders.WithWorkflowGraph().AsTracking().FirstOrDefaultAsync(row => row.LinkedCrossTenantId == correlation, cancellationToken);
        if (request is null || request.Supplier.LinkedTenantId != delivery.SourceTenantId)
        {
            this.log.LogWarning("The job message {MessageId} of tenant {SourceTenantId} names no request of tenant {TenantId} sent to it; it changes nothing.", delivery.MessageId, delivery.SourceTenantId, delivery.TargetTenantId);
            return;
        }

        var target = OrderSync.RequestStateFor(payload.State, payload.Confirmed);
        if (target is { } forward && OrderSync.MovesForward(request.State, forward))
        {
            var step = OrderWorkflowAttribute.FollowStepFrom(request.State, forward);
            if (WorkflowRegistry.Default.Get<OrderState, Order>().TryGetTransition(step, out _))
            {
                var workflows = delivery.Services.GetRequiredService<WorkflowService>();
                await workflows.RunSystemStepInTransactionAsync<OrderState, Order>(context, request, step, cancellationToken, delivery.AfterCommit);
            }
            else
            {
                this.log.LogWarning("The order {RequestId} is {State}, and no step leads from there to {Target}; the message {MessageId} changes nothing.", request.Id, request.State, forward, delivery.MessageId);
            }
        }
        else
        {
            this.log.LogInformation("The order {RequestId} is {State}; the job's {JobState} moves it no further.", request.Id, request.State, payload.State);
        }

        if (payload.StaffMembers is { } names && target == request.State && OrderSync.ShowsStaffMembers(request.State))
        {
            await ReplaceStaffMembersAsync(context, request, names, cancellationToken);
        }
    }

    private static async Task ReplaceStaffMembersAsync(ApplicationDbContext context, Order request, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var requestId = request.Id;
        var held = await context.OrderStaff.AsTracking()
            .Where(staff => staff.OrderId == requestId)
            .OrderBy(staff => staff.Position)
            .ToListAsync(cancellationToken);
        var kept = OrderSync.StaffNames(names.Select(name => ((string?)name, (string?)null)));
        if (held.Select(staff => staff.Name).SequenceEqual(kept, StringComparer.Ordinal))
        {
            return;
        }

        context.OrderStaff.RemoveRange(held);
        context.OrderStaff.AddRange(kept.Select((name, position) => new OrderStaff
        {
            TenantId = request.TenantId,
            OrderId = requestId,
            Name = name,
            Position = position,
        }));
    }
}

public sealed class OrderCancelHandler : IIntegrationHandler
{
    private readonly ILogger<OrderCancelHandler> log;

    public OrderCancelHandler(ILogger<OrderCancelHandler> log)
    {
        this.log = log;
    }

    public async Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var stepLock in delivery.Services.GetServices<IStepLock>().Where(stepLock => stepLock.EntityType.IsAssignableFrom(typeof(Job))))
        {
            await stepLock.TakeAsync(context, null, cancellationToken);
        }

        var correlation = delivery.CorrelationId;
        var job = await context.Jobs.WithWorkflowGraph().AsTracking().FirstOrDefaultAsync(row => row.LinkedCrossTenantId == correlation, cancellationToken);
        if (job is null || job.Customer.LinkedTenantId != delivery.SourceTenantId)
        {
            this.log.LogWarning("The cancellation {MessageId} of tenant {SourceTenantId} names no job of tenant {TenantId} its portal sent; it changes nothing.", delivery.MessageId, delivery.SourceTenantId, delivery.TargetTenantId);
            return;
        }

        if (!JobWorkflowAttribute.Cancelable.Any(cancelable => cancelable.State == job.State))
        {
            this.log.LogInformation("The job {JobId} is {State}, so the customer's cancellation {MessageId} leaves it as it is and answers with its state.", job.Id, job.State, delivery.MessageId);
            await delivery.Services.GetRequiredService<IJobStateSync>().QueueAnswerAsync(context, job, cancellationToken);
            return;
        }

        var touched = TenantChangeCollector.Attach(context, delivery.TargetTenantId);
        var workflows = delivery.Services.GetRequiredService<WorkflowService>();
        await workflows.RunSystemStepInTransactionAsync<JobState, Job>(context, job, JobWorkflowAttribute.CanceledInPortalStepFrom(job.State), cancellationToken, delivery.AfterCommit);
        await touched.NameAffectedAsync(context, this.log, CancellationToken.None);
        var changes = delivery.Services.GetRequiredService<TenantChangeBus>();
        delivery.AfterCommit.Add(_ =>
        {
            touched.PublishTo(changes);
            return Task.CompletedTask;
        });
    }
}
