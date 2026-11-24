using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public sealed partial class OrderSyncTests
{
    [Fact]
    public async Task A_cancellation_that_reaches_a_completed_job_ends_completed_on_both_sides()
    {
        var s = await this.ScenarioAsync("Late Cancel Staffing");
        var requestId = await this.SendAsync(s, "Completed while held");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Coordinating, (await this.RequestAsync(requestId)).State);

        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        var assignment = await this.AssignAsync(s, slot, tess);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);
        await this.SetTenantStateAsync(s.PortalId, TenantState.Suspended);
        s.Clock.Advance(new DateTimeOffset(2027, 2, 1, 10, 0, 0, TimeSpan.Zero) - s.Clock.GetUtcNow());
        await this.ClockPassAsync(s);
        s.Clock.Advance(new DateTimeOffset(2027, 6, 1, 10, 0, 0, TimeSpan.Zero) - s.Clock.GetUtcNow());
        await this.ClockPassAsync(s);
        Assert.Equal(JobState.Completed, (await this.StoredJobAsync(jobId)).State);
        await this.SetTenantStateAsync(s.PortalId, TenantState.Active);
        Assert.Equal(OrderState.Coordinating, (await this.RequestAsync(requestId)).State);

        var cancel = await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating));
        Assert.True(cancel.IsSuccess, cancel.Error);
        var waiting = await this.PageAsync(s, requestId);
        Assert.Equal(OrderState.CancellationSent, waiting.State);
        Assert.Null(waiting.StaffMembers);
        var jobMessages = await this.JobMessagesAsync(s);

        await this.DeliverAsync(s);
        var ended = await this.PageAsync(s, requestId);
        Assert.Equal(OrderState.Completed, ended.State);
        Assert.Equal(["Tess Example"], ended.StaffMembers);
        Assert.Equal(JobState.Completed, (await this.StoredJobAsync(jobId)).State);
        Assert.Equal([SlotAssignmentState.Assigned], await AssignmentScenario.StatesAsync(this.database, Ct, assignment));
        Assert.Equal(jobMessages + 1, await this.JobMessagesAsync(s));
        var steps = (await this.HistoryAsync(requestId)).Select(entry => entry.TransitionKey).ToList();
        Assert.Equal(
            [OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating), OrderWorkflowAttribute.FollowStepFrom(OrderState.CancellationSent, OrderState.Completed)],
            steps[^2..]);

        Assert.Equal([MessageTypes.OrderCompleted.Key], await this.NoticesToAsync(s.PortalId, s.OrdererId));
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(0, await system.StoredMessages.CountAsync(stored => stored.TenantId == s.SupplierId && stored.MessageType == MessageTypes.JobCanceledInPortal.Key, Ct));
        Assert.DoesNotContain(MessageTypes.AssignmentCanceled.Key, await this.NoticesToAsync(s.SupplierId, tess));
    }

    [Fact]
    public async Task A_request_reads_cancellation_sent_while_its_messages_are_held_and_ends_canceled_with_its_job()
    {
        var s = await this.ScenarioAsync("Held Cancel Staffing");
        var requestId = await this.SendAsync(s, "Held cancellation");
        var jobId = await this.JobOfAsync(requestId);
        await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        await this.DeliverAsync(s);

        var cancel = await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating));
        Assert.True(cancel.IsSuccess, cancel.Error);
        await this.SetTenantStateAsync(s.SupplierId, TenantState.Suspended);
        for (var pass = 0; pass < 3; pass++)
        {
            s.Clock.Advance(TimeSpan.FromHours(1));
            await this.PassAsync(s);
        }

        Assert.Equal(OrderState.CancellationSent, (await this.PageAsync(s, requestId)).State);
        Assert.Equal(JobState.Coordinating, (await this.StoredJobAsync(jobId)).State);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Equal(IntegrationMessageState.Pending, await system.IntegrationMessages.AsNoTracking().Where(message => message.TenantId == s.PortalId && message.Kind == OrderSync.CancelKind).Select(message => message.State).SingleAsync(Ct));
        }

        await this.SetTenantStateAsync(s.SupplierId, TenantState.Active);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.CanceledByUs, (await this.RequestAsync(requestId)).State);
        Assert.Equal(JobState.CanceledByCustomer, (await this.StoredJobAsync(jobId)).State);
        Assert.Equal(OrderWorkflowAttribute.FollowStepFrom(OrderState.CancellationSent, OrderState.CanceledByUs), (await this.HistoryAsync(requestId))[^1].TransitionKey);
    }

    [Fact]
    public async Task A_portals_cancellation_tells_the_coordinator_who_still_coordinates_and_every_coordinator_once_the_coordinator_no_longer_does()
    {
        var s = await this.ScenarioAsync("Role Cancel Staffing");
        var owner = s.Staffing.StaffMembers.Onboarded.Tenant.OwnerId;

        var kept = await this.SendAsync(s, "Kept coordinator job");
        var keptJob = await this.JobOfAsync(kept);
        await this.PrepareAsync(s, keptJob);
        await this.JobStepAsync(s, keptJob, JobWorkflowAttribute.StartStep);
        Assert.True((await this.RequestStepAsync(s, kept, OrderWorkflowAttribute.CancelStepFrom(OrderState.Sent))).IsSuccess);
        await this.DeliverAsync(s);
        Assert.Single(await this.NoticesToAsync(s.SupplierId, s.Coordinator), type => type == MessageTypes.JobCanceledInPortal.Key);
        Assert.DoesNotContain(MessageTypes.JobCanceledInPortal.Key, await this.NoticesToAsync(s.SupplierId, owner));

        var lost = await this.SendAsync(s, "Lost coordinator job");
        var lostJob = await this.JobOfAsync(lost);
        await this.PrepareAsync(s, lostJob);
        await this.JobStepAsync(s, lostJob, JobWorkflowAttribute.StartStep);
        await using (var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var holding = await StaffScenario.HoldingIdAsync(this.database, s.Coordinator, s.Staffing.StaffMembers.Onboarded.Roles.Coordinator, s.Staffing.StaffMembers.RootId, Ct);
            var removed = await host.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding);
            Assert.True(removed.IsSuccess, removed.Error);
        }

        Assert.True((await this.RequestStepAsync(s, lost, OrderWorkflowAttribute.CancelStepFrom(OrderState.Sent))).IsSuccess);
        await this.DeliverAsync(s);
        Assert.Equal(JobState.CanceledByCustomer, (await this.StoredJobAsync(lostJob)).State);
        Assert.Single(await this.NoticesToAsync(s.SupplierId, owner), type => type == MessageTypes.JobCanceledInPortal.Key);
        Assert.Single(await this.NoticesToAsync(s.SupplierId, s.Coordinator), type => type == MessageTypes.JobCanceledInPortal.Key);
    }

    private async Task<int> JobMessagesAsync(SyncScenario s)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.IntegrationMessages.CountAsync(message => message.TenantId == s.SupplierId && message.TargetTenantId == s.PortalId && message.Kind == OrderSync.JobStateKind, Ct);
    }

    private async Task SetTenantStateAsync(Guid tenantId, TenantState state)
    {
        await using var system = this.database.CreateSystemContext();
        await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)state} WHERE Id = {tenantId}", Ct);
    }
}
