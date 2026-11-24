using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Integration;
using Staffora.Services.Portal;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed partial class OrderSyncTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Start = new(2027, 2, 1);
    private static readonly DateOnly End = new(2027, 5, 31);

    private static readonly DateTimeOffset Anchor = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public OrderSyncTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_request_follows_its_job_to_completed_telling_the_orderer_and_showing_the_staff_member_from_the_confirmation_on()
    {
        var s = await this.ScenarioAsync("Follow Staffing");
        var requestId = await this.SendAsync(s, "Algebra for year 9");
        var jobId = await this.JobOfAsync(requestId);

        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.BackToRegistrationStep);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.HandOverStep);
        Assert.Equal(0, await this.MessagesAsync(s.SupplierId));
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Sent, (await this.RequestAsync(requestId)).State);

        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Coordinating, (await this.RequestAsync(requestId)).State);
        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        await this.AssignAsync(s, slot, tess);
        Assert.Equal(1, await this.MessagesAsync(s.SupplierId));
        var staffed = await this.PageAsync(s, requestId);
        Assert.Equal(OrderState.Coordinating, staffed.State);
        Assert.Null(staffed.StaffMembers);

        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);
        await this.DeliverAsync(s);
        var confirmed = await this.PageAsync(s, requestId);
        Assert.Equal(OrderState.Confirmed, confirmed.State);
        Assert.Equal(["Tess Example"], confirmed.StaffMembers);

        s.Clock.Advance(new DateTimeOffset(2027, 2, 1, 10, 0, 0, TimeSpan.Zero) - s.Clock.GetUtcNow());
        await this.ClockPassAsync(s);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.InProgress, (await this.RequestAsync(requestId)).State);
        s.Clock.Advance(new DateTimeOffset(2027, 6, 1, 10, 0, 0, TimeSpan.Zero) - s.Clock.GetUtcNow());
        await this.ClockPassAsync(s);
        await this.DeliverAsync(s);
        var completed = await this.PageAsync(s, requestId);
        Assert.Equal(OrderState.Completed, completed.State);
        Assert.Equal(["Tess Example"], completed.StaffMembers);

        var portalAccount = SystemAccount.IdFor(s.PortalId);
        var history = await this.HistoryAsync(requestId);
        Assert.Equal(
            [
                OrderWorkflowAttribute.SaveDraftStep, OrderWorkflowAttribute.SendStep,
                OrderWorkflowAttribute.FollowStepFrom(OrderState.Sent, OrderState.Coordinating),
                OrderWorkflowAttribute.FollowStepFrom(OrderState.Coordinating, OrderState.Confirmed),
                OrderWorkflowAttribute.FollowStepFrom(OrderState.Confirmed, OrderState.InProgress),
                OrderWorkflowAttribute.FollowStepFrom(OrderState.InProgress, OrderState.Completed),
            ],
            history.Select(entry => entry.TransitionKey));
        Assert.All(history.Skip(2), entry => Assert.Equal((s.PortalId, portalAccount, portalAccount, (Guid?)null), (entry.TenantId, entry.ActorId, entry.CreatedById, entry.ImpersonatedById)));
        var request = await this.RequestAsync(requestId);
        Assert.Equal(((Guid?)portalAccount, (Guid?)null), (request.ModifiedById, request.ImpersonatedById));
        var names = await this.StaffRowsAsync(requestId);
        Assert.All(names, row => Assert.Equal((s.PortalId, portalAccount, (Guid?)null), (row.TenantId, row.CreatedById, row.ImpersonatedById)));

        Assert.Equal([MessageTypes.OrderConfirmed.Key, MessageTypes.OrderCompleted.Key], await this.NoticesToAsync(s.PortalId, s.OrdererId));
        var notices = await this.NoticesOfAsync(s.PortalId);
        Assert.All(notices, notice => Assert.Equal((OutgoingMessageStatus.Delivered, $"?dlg=order-{requestId}", (Guid?)portalAccount), (notice.Status, notice.Link, notice.CreatedById)));
    }

    [Fact]
    public async Task A_request_the_staffing_company_declines_or_cancels_tells_the_orderer_and_its_own_states_never_show()
    {
        var s = await this.ScenarioAsync("Decline Staffing");

        var declined = await this.SendAsync(s, "Declined job");
        var declinedJob = await this.JobOfAsync(declined);
        await this.JobStepAsync(s, declinedJob, JobWorkflowAttribute.BackToRegistrationStep);
        await this.JobStepAsync(s, declinedJob, JobWorkflowAttribute.CancelStepFrom(JobState.Registration));

        var canceled = await this.SendAsync(s, "Canceled job");
        var canceledJob = await this.JobOfAsync(canceled);
        var slot = await this.PrepareAsync(s, canceledJob);
        await this.JobStepAsync(s, canceledJob, JobWorkflowAttribute.StartStep);
        await this.AssignAsync(s, slot, await s.Staffing.AddStaffAsync(this.database, "Tess", Ct));
        await this.JobStepAsync(s, canceledJob, JobWorkflowAttribute.ConfirmStep);
        await this.JobStepAsync(s, canceledJob, JobWorkflowAttribute.CancelStepFrom(JobState.Confirmed));

        await this.DeliverAsync(s);
        Assert.Equal(OrderState.DeclinedBySupplier, (await this.RequestAsync(declined)).State);
        Assert.Equal(
            [OrderWorkflowAttribute.SaveDraftStep, OrderWorkflowAttribute.SendStep, OrderWorkflowAttribute.FollowStepFrom(OrderState.Sent, OrderState.DeclinedBySupplier)],
            (await this.HistoryAsync(declined)).Select(entry => entry.TransitionKey));
        var canceledPage = await this.PageAsync(s, canceled);
        Assert.Equal(OrderState.CanceledBySupplier, canceledPage.State);
        Assert.Null(canceledPage.StaffMembers);

        Assert.Equal(
            [MessageTypes.OrderDeclined.Key, MessageTypes.OrderConfirmed.Key, MessageTypes.OrderCanceledBySupplier.Key],
            await this.NoticesToAsync(s.PortalId, s.OrdererId));

        await using var system = this.database.CreateSystemContext();
        var payloads = await system.IntegrationMessages.AsNoTracking().Where(message => message.TenantId == s.SupplierId).Select(message => message.Payload).ToListAsync(Ct);
        Assert.All(payloads, payload => Assert.True(OrderSync.IsCustomerVisible(OrderSync.ReadJobState(payload)!.State)));
    }

    [Fact]
    public async Task A_late_repeated_or_overtaking_message_never_moves_a_request_twice_or_back()
    {
        var s = await this.ScenarioAsync("Order Staffing");
        var requestId = await this.SendAsync(s, "Ordered job");
        var link = (await this.RequestAsync(requestId)).LinkedCrossTenantId!.Value;

        await this.QueueAsync(s, link, new JobStatePayload(JobState.Confirmed, false, ["Tess Example"]));
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Confirmed, (await this.RequestAsync(requestId)).State);
        var before = await this.HistoryAsync(requestId);
        Assert.Equal(OrderWorkflowAttribute.FollowStepFrom(OrderState.Sent, OrderState.Confirmed), before[^1].TransitionKey);

        await this.QueueAsync(s, link, new JobStatePayload(JobState.Coordinating, false, null));
        await this.QueueAsync(s, link, new JobStatePayload(JobState.Confirmed, false, ["Tess Example"]));
        await this.QueueAsync(s, link, new JobStatePayload(JobState.CanceledBySupplier, false, null));
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Confirmed, (await this.RequestAsync(requestId)).State);
        Assert.Equal(before.Select(entry => entry.Id), (await this.HistoryAsync(requestId)).Select(entry => entry.Id));
        Assert.Equal([MessageTypes.OrderConfirmed.Key], await this.NoticesToAsync(s.PortalId, s.OrdererId));
        Assert.Equal(["Tess Example"], (await this.PageAsync(s, requestId)).StaffMembers);

        await this.QueueAsync(s, link, new JobStatePayload(JobState.Completed, true, ["Tess Example"]));
        await this.QueueAsync(s, link, new JobStatePayload(JobState.InProgress, true, ["Uma Example"]));
        await this.DeliverAsync(s);
        var final = await this.PageAsync(s, requestId);
        Assert.Equal(OrderState.Completed, final.State);
        Assert.Equal(["Tess Example"], final.StaffMembers);
        Assert.Equal(before.Count + 1, (await this.HistoryAsync(requestId)).Count);
    }

    [Fact]
    public async Task A_delivery_rolled_back_after_its_step_leaves_the_request_where_it_was_and_its_retry_moves_it_once()
    {
        var s = await this.ScenarioAsync("Rollback Staffing");
        var requestId = await this.SendAsync(s, "Rolled back job");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        await this.AssignAsync(s, slot, await s.Staffing.AddStaffAsync(this.database, "Tess", Ct));
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);

        var fault = new MarkFault(s.SupplierId);
        await this.PassAsync(s, [fault]);
        Assert.Equal(1, fault.Struck);
        await using (var system = this.database.CreateSystemContext())
        {
            var first = await system.IntegrationMessages.AsNoTracking().Where(message => message.TenantId == s.SupplierId).OrderBy(message => message.Sequence).FirstAsync(Ct);
            Assert.Equal((IntegrationMessageState.Pending, 1, nameof(InvalidOperationException)), (first.State, first.Attempts, first.LastFailure));
        }

        Assert.Equal(OrderState.Sent, (await this.RequestAsync(requestId)).State);
        Assert.Equal(2, (await this.HistoryAsync(requestId)).Count);
        Assert.Empty(await this.NoticesOfAsync(s.PortalId));
        Assert.Empty(await this.StaffRowsAsync(requestId));
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Equal(0, await system.IntegrationReceipts.CountAsync(receipt => receipt.TenantId == s.PortalId, Ct));
        }

        s.Clock.Advance(IntegrationRetryRules.FirstRetry);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Confirmed, (await this.RequestAsync(requestId)).State);
        Assert.Equal(
            [OrderWorkflowAttribute.FollowStepFrom(OrderState.Sent, OrderState.Coordinating), OrderWorkflowAttribute.FollowStepFrom(OrderState.Coordinating, OrderState.Confirmed)],
            (await this.HistoryAsync(requestId)).Skip(2).Select(entry => entry.TransitionKey));
        Assert.Equal([MessageTypes.OrderConfirmed.Key], await this.NoticesToAsync(s.PortalId, s.OrdererId));
        Assert.Single(await this.StaffRowsAsync(requestId));
    }

    [Fact]
    public async Task The_staff_members_cross_by_name_alone_and_change_on_the_request_as_the_confirmed_jobs_assignments_change()
    {
        var s = await this.ScenarioAsync("Names Staffing");
        var requestId = await this.SendAsync(s, "Named job");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        var tessAssignment = await this.AssignAsync(s, slot, tess);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);
        await this.DeliverAsync(s);
        Assert.Equal(["Tess Example"], (await this.PageAsync(s, requestId)).StaffMembers);

        await using (var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var canceled = await AssignmentScenario.StepAsync(host.Get<SlotAssignmentService>(), tessAssignment, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct);
            Assert.True(canceled.IsSuccess, canceled.Error);
        }

        await this.DeliverAsync(s);
        Assert.Empty((await this.PageAsync(s, requestId)).StaffMembers!);
        var uma = await s.Staffing.AddStaffAsync(this.database, "Uma", Ct);
        await this.AssignAsync(s, slot, uma);
        await this.DeliverAsync(s);
        Assert.Equal(["Uma Example"], (await this.PageAsync(s, requestId)).StaffMembers);
        Assert.Equal(["Uma Example"], (await this.StaffRowsAsync(requestId)).Select(row => row.Name));

        await using var system = this.database.CreateSystemContext();
        var people = await system.Users.AsNoTracking().Where(user => user.Id == tess || user.Id == uma).Select(user => new { user.Id, user.Email, user.PhoneNumber }).ToListAsync(Ct);
        var records = await system.Resources.AsNoTracking().Where(resource => resource.UserId == tess || resource.UserId == uma).Select(resource => resource.Id).ToListAsync(Ct);
        var payloads = await system.IntegrationMessages.AsNoTracking().Where(message => message.TenantId == s.SupplierId).Select(message => message.Payload).ToListAsync(Ct);
        Assert.Equal(4, payloads.Count);
        var secrets = people.SelectMany(person => new[] { person.Id.ToString(), person.Email, person.PhoneNumber }).Concat(records.Select(id => id.ToString())).OfType<string>().Where(secret => secret.Length > 0).ToList();
        Assert.All(payloads, payload =>
        {
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, payload, StringComparison.OrdinalIgnoreCase));
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            Assert.Equal(["state", "confirmed", "staffMembers"], json.RootElement.EnumerateObject().Select(property => property.Name));
        });
    }

    [Fact]
    public async Task A_canceled_request_cancels_its_job_freeing_and_telling_its_staff_members_and_its_coordinator()
    {
        var s = await this.ScenarioAsync("Cancel Staffing");
        var requestId = await this.SendAsync(s, "Canceled by the customer");
        var jobId = await this.JobOfAsync(requestId);
        var asked = await this.PrepareAsync(s, jobId);
        var held = await this.AddSlotAsync(s, jobId, "Second staff member");
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        var uma = await s.Staffing.AddStaffAsync(this.database, "Uma", Ct);
        var requested = await this.RequestStaffAsync(s, asked, tess);
        var assigned = await this.AssignAsync(s, held, uma);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Coordinating, (await this.RequestAsync(requestId)).State);

        var (_, otherSlots) = await s.Staffing.StartedJobAsync(this.database, "Other job", Start, End, Ct);
        Assert.False((await s.Staffing.AssignAsync(this.database, s.Coordinator, otherSlots[0], uma, Ct)).IsSuccess);

        var cancel = await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating));
        Assert.True(cancel.IsSuccess, cancel.Error);
        Assert.Equal(OrderState.CancellationSent, (await this.RequestAsync(requestId)).State);
        await using (var system = this.database.CreateSystemContext())
        {
            var message = await system.IntegrationMessages.AsNoTracking().SingleAsync(row => row.TenantId == s.PortalId, Ct);
            Assert.Equal((OrderSync.CancelKind, s.SupplierId, (await this.RequestAsync(requestId)).CrossTenantId!.Value), (message.Kind, message.TargetTenantId, message.CorrelationId));
        }

        await this.DeliverAsync(s);
        var supplierAccount = SystemAccount.IdFor(s.SupplierId);
        await using (var system = this.database.CreateSystemContext())
        {
            var job = await system.Jobs.AsNoTracking().SingleAsync(row => row.Id == jobId, Ct);
            Assert.Equal(JobState.CanceledByCustomer, job.State);
            var step = await system.StateHistoryEntries.AsNoTracking().SingleAsync(entry => entry.EntityId == jobId && entry.ToState == (int)JobState.CanceledByCustomer, Ct);
            Assert.Equal((JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.Coordinating), supplierAccount, supplierAccount, (Guid?)null), (step.TransitionKey, step.ActorId, step.CreatedById, step.ImpersonatedById));
            Assert.All(await system.JobSlots.AsNoTracking().Where(slot => slot.JobId == jobId).ToListAsync(Ct), slot => Assert.Equal(JobSlotState.Canceled, slot.State));
        }

        Assert.Equal([SlotAssignmentState.Canceled, SlotAssignmentState.Canceled], await AssignmentScenario.StatesAsync(this.database, Ct, requested, assigned));
        Assert.Equal(OrderState.CanceledByUs, (await this.RequestAsync(requestId)).State);
        Assert.Equal(
            [OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating), OrderWorkflowAttribute.FollowStepFrom(OrderState.CancellationSent, OrderState.CanceledByUs)],
            (await this.HistoryAsync(requestId))[^2..].Select(entry => entry.TransitionKey));

        Assert.True((await s.Staffing.AssignAsync(this.database, s.Coordinator, otherSlots[0], uma, Ct)).IsSuccess);
        Assert.Contains(MessageTypes.AssignmentCanceled.Key, await this.NoticesToAsync(s.SupplierId, tess));
        Assert.Contains(MessageTypes.AssignmentCanceled.Key, await this.NoticesToAsync(s.SupplierId, uma));
        Assert.Equal([MessageTypes.JobCanceledInPortal.Key], (await this.NoticesToAsync(s.SupplierId, s.Coordinator)).Where(type => type == MessageTypes.JobCanceledInPortal.Key));
    }

    [Fact]
    public async Task The_orderer_cancels_a_draft_and_a_request_being_staffed_but_never_a_confirmed_one()
    {
        var s = await this.ScenarioAsync("Cut-off Staffing");

        var draft = await this.WriteAsync(s, "Draft job");
        var dropped = await this.RequestStepAsync(s, draft, OrderWorkflowAttribute.CancelStepFrom(OrderState.Registration));
        Assert.True(dropped.IsSuccess, dropped.Error);
        Assert.Equal(OrderState.CanceledByUs, (await this.RequestAsync(draft)).State);
        Assert.Equal(0, await this.MessagesAsync(s.PortalId));

        var requestId = await this.SendAsync(s, "Confirmed job");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        await this.AssignAsync(s, slot, await s.Staffing.AddStaffAsync(this.database, "Tess", Ct));
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.Confirmed, (await this.RequestAsync(requestId)).State);
        foreach (var (from, _) in OrderWorkflowAttribute.Cancelable)
        {
            var refused = await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.CancelStepFrom(from));
            Assert.False(refused.IsSuccess);
        }

        Assert.Equal(OrderState.Confirmed, (await this.RequestAsync(requestId)).State);
        Assert.Equal(0, await this.MessagesAsync(s.PortalId));

        var forced = await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.FollowStepFrom(OrderState.Confirmed, OrderState.CanceledByUs));
        Assert.False(forced.IsSuccess);
        Assert.Equal(OrderState.Confirmed, (await this.RequestAsync(requestId)).State);
    }

    [Fact]
    public async Task A_cancellation_racing_the_confirmation_ends_canceled_on_both_sides()
    {
        var s = await this.ScenarioAsync("Race Staffing");
        var requestId = await this.SendAsync(s, "Raced job");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        var assignment = await this.AssignAsync(s, slot, tess);
        await this.DeliverAsync(s);

        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);
        var cancel = await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating));
        Assert.True(cancel.IsSuccess, cancel.Error);

        await this.DeliverAsync(s);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.CanceledByUs, (await this.RequestAsync(requestId)).State);
        Assert.DoesNotContain((await this.HistoryAsync(requestId)).Select(entry => entry.ToState), state => state == (int)OrderState.Confirmed);
        Assert.Empty(await this.NoticesToAsync(s.PortalId, s.OrdererId));
        Assert.Empty(await this.StaffRowsAsync(requestId));
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(JobState.CanceledByCustomer, await system.Jobs.AsNoTracking().Where(row => row.Id == jobId).Select(row => row.State).SingleAsync(Ct));
        Assert.Equal(JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.Confirmed), await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == jobId && entry.ToState == (int)JobState.CanceledByCustomer).Select(entry => entry.TransitionKey).SingleAsync(Ct));
        Assert.Equal([SlotAssignmentState.Canceled], await AssignmentScenario.StatesAsync(this.database, Ct, assignment));
        Assert.Contains(MessageTypes.AssignmentCanceled.Key, await this.NoticesToAsync(s.SupplierId, tess));
    }

    [Fact]
    public async Task A_message_that_names_another_portals_request_or_job_moves_nothing()
    {
        var s = await this.ScenarioAsync("Stranger Staffing");
        var root = s.Staffing.StaffMembers.RootId;
        var otherName = TwoTenantScenario.UniqueName("Harbor Learning");
        var otherCustomer = await PortalScenario.AddActiveCustomerAsync(this.database, s.Staffing.StaffMembers.Onboarded.OwnerRuntime(), otherName, Ct);
        var other = await PortalScenario.ForCustomerAsync(this.database, s.Staffing.StaffMembers.Onboarded, otherCustomer, otherName, root, root, Ct);
        var requestId = await this.SendAsync(s, "Kept job");
        var request = await this.RequestAsync(requestId);
        var jobId = await this.JobOfAsync(requestId);

        await this.QueueFromAsync(s, other.AdministratorRuntime(), OrderSync.CancelKind, s.SupplierId, request.CrossTenantId!.Value, "{}");

        var staffing = s.Staffing.StaffMembers.Onboarded.OwnerRuntime();
        await this.QueueFromAsync(s, staffing, OrderSync.JobStateKind, other.PortalTenantId, request.LinkedCrossTenantId!.Value, OrderSync.WriteJobState(new JobStatePayload(JobState.Confirmed, false, ["Tess Example"])));
        await this.QueueFromAsync(s, staffing, OrderSync.JobStateKind, s.PortalId, Guid.NewGuid(), OrderSync.WriteJobState(new JobStatePayload(JobState.Confirmed, false, ["Tess Example"])));
        await this.QueueFromAsync(s, staffing, OrderSync.JobStateKind, s.PortalId, request.LinkedCrossTenantId!.Value, "not a job state");
        await this.DeliverAsync(s, other.PortalTenantId);

        await using var system = this.database.CreateSystemContext();
        Assert.All(
            await system.IntegrationMessages.AsNoTracking().Where(message => message.TenantId == s.SupplierId || message.TenantId == other.PortalTenantId).ToListAsync(Ct),
            message => Assert.Equal(IntegrationMessageState.Delivered, message.State));
        Assert.Equal(OrderState.Sent, (await this.RequestAsync(requestId)).State);
        Assert.Equal(2, (await this.HistoryAsync(requestId)).Count);
        Assert.Empty(await this.StaffRowsAsync(requestId));
        Assert.Equal(JobState.AwaitingCoordination, await system.Jobs.AsNoTracking().Where(row => row.Id == jobId).Select(row => row.State).SingleAsync(Ct));
        Assert.Equal(0, await system.StoredMessages.CountAsync(stored => stored.MessageType == MessageTypes.JobCanceledInPortal.Key && stored.TenantId == s.SupplierId, Ct));
        Assert.Empty(await this.NoticesToAsync(s.PortalId, s.OrdererId));
        Assert.Equal(0, await system.Orders.CountAsync(row => row.TenantId == other.PortalTenantId, Ct));
    }

    [Fact]
    public async Task A_request_canceled_while_nobody_coordinates_its_job_tells_every_coordinator_and_a_deleted_job_reads_declined()
    {
        var s = await this.ScenarioAsync("Unstaffed Staffing");
        var owner = s.Staffing.StaffMembers.Onboarded.Tenant.OwnerId;

        var waiting = await this.SendAsync(s, "Waiting job");
        var waitingJob = await this.JobOfAsync(waiting);
        Assert.True((await this.RequestStepAsync(s, waiting, OrderWorkflowAttribute.CancelStepFrom(OrderState.Sent))).IsSuccess);

        var deleted = await this.SendAsync(s, "Deleted job");
        var deletedJob = await this.JobOfAsync(deleted);
        await this.JobStepAsync(s, deletedJob, JobWorkflowAttribute.BackToRegistrationStep);
        await this.JobStepAsync(s, deletedJob, JobWorkflowAttribute.DeleteStep);
        var left = await this.SendAsync(s, "Left job");
        var leftJob = await this.JobOfAsync(left);
        await this.PrepareAsync(s, leftJob);
        await this.JobStepAsync(s, leftJob, JobWorkflowAttribute.StartStep);
        await using (var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var deactivated = await host.Get<Staffora.Services.Services.UserService>().StepUserAsync(s.Coordinator, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.True(deactivated.IsSuccess, deactivated.Error);
        }

        Assert.True((await this.RequestStepAsync(s, left, OrderWorkflowAttribute.CancelStepFrom(OrderState.Sent))).IsSuccess);

        await this.DeliverAsync(s);
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(
            [JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.AwaitingCoordination), JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.Coordinating)],
            await system.StateHistoryEntries.AsNoTracking().Where(entry => (entry.EntityId == waitingJob || entry.EntityId == leftJob) && entry.ToState == (int)JobState.CanceledByCustomer).OrderBy(entry => entry.EntityId == leftJob).Select(entry => entry.TransitionKey).ToListAsync(Ct));
        Assert.Equal([MessageTypes.JobCanceledInPortal.Key, MessageTypes.JobCanceledInPortal.Key], (await this.NoticesToAsync(s.SupplierId, owner)).Where(type => type == MessageTypes.JobCanceledInPortal.Key));
        Assert.False(await system.Jobs.AnyAsync(row => row.Id == deletedJob, Ct));
        Assert.Equal(OrderState.DeclinedBySupplier, (await this.RequestAsync(deleted)).State);
        Assert.Equal([MessageTypes.OrderDeclined.Key], await this.NoticesToAsync(s.PortalId, s.OrdererId));
    }

    [Fact]
    public async Task A_confirmation_delivered_while_the_orderer_cancels_ends_canceled_on_both_sides()
    {
        var s = await this.ScenarioAsync("Interleaved Staffing");
        var requestId = await this.SendAsync(s, "Interleaved job");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        await this.AssignAsync(s, slot, tess);
        await this.DeliverAsync(s);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.ConfirmStep);

        var cancel = new BeforeRequestLock(async () =>
        {
            var canceled = await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating));
            Assert.True(canceled.IsSuccess, canceled.Error);
        });
        await this.PassAsync(s, [cancel]);
        Assert.Equal(1, cancel.Struck);
        Assert.Equal(OrderState.CancellationSent, (await this.RequestAsync(requestId)).State);

        s.Clock.Advance(IntegrationRetryRules.FirstRetry);
        await this.DeliverAsync(s);
        Assert.Equal(OrderState.CanceledByUs, (await this.RequestAsync(requestId)).State);
        Assert.DoesNotContain((await this.HistoryAsync(requestId)).Select(entry => entry.ToState), state => state == (int)OrderState.Confirmed);
        Assert.Empty(await this.NoticesToAsync(s.PortalId, s.OrdererId));
        Assert.Empty(await this.StaffRowsAsync(requestId));
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(JobState.CanceledByCustomer, await system.Jobs.AsNoTracking().Where(row => row.Id == jobId).Select(row => row.State).SingleAsync(Ct));
    }

    [Fact]
    public async Task A_second_dispatcher_taking_over_a_delivery_moves_the_request_once()
    {
        var s = await this.ScenarioAsync("Takeover Staffing");
        var requestId = await this.SendAsync(s, "Taken over job");
        var jobId = await this.JobOfAsync(requestId);
        await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);

        var takeover = new BeforeRequestLock(async () =>
        {
            s.Clock.Advance(IntegrationRetryRules.ClaimLease + TimeSpan.FromSeconds(1));
            await this.PassAsync(s);
        });
        await this.PassAsync(s, [takeover]);
        Assert.Equal(1, takeover.Struck);

        Assert.Equal(
            [OrderWorkflowAttribute.SaveDraftStep, OrderWorkflowAttribute.SendStep, OrderWorkflowAttribute.FollowStepFrom(OrderState.Sent, OrderState.Coordinating)],
            (await this.HistoryAsync(requestId)).Select(entry => entry.TransitionKey));
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(1, await system.IntegrationReceipts.CountAsync(receipt => receipt.TenantId == s.PortalId, Ct));
        var message = await system.IntegrationMessages.AsNoTracking().SingleAsync(row => row.TenantId == s.SupplierId, Ct);
        Assert.Equal((IntegrationMessageState.Delivered, (string?)null), (message.State, message.LastFailure));
    }

    [Fact]
    public async Task A_cancellation_rolled_back_at_its_mark_cancels_nothing_and_tells_nobody_and_its_retry_cancels_once()
    {
        var s = await this.ScenarioAsync("Cancel Rollback Staffing");
        var requestId = await this.SendAsync(s, "Rolled back cancellation");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        var tess = await s.Staffing.AddStaffAsync(this.database, "Tess", Ct);
        var assignment = await this.AssignAsync(s, slot, tess);
        await this.DeliverAsync(s);
        Assert.True((await this.RequestStepAsync(s, requestId, OrderWorkflowAttribute.CancelStepFrom(OrderState.Coordinating))).IsSuccess);

        var fault = new MarkFault(s.PortalId, OrderSync.CancelKind);
        await this.PassAsync(s, [fault]);
        Assert.Equal(1, fault.Struck);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Equal(JobState.Coordinating, await system.Jobs.AsNoTracking().Where(row => row.Id == jobId).Select(row => row.State).SingleAsync(Ct));
            Assert.Equal(0, await system.OutgoingMessages.CountAsync(notice => notice.TenantId == s.SupplierId && notice.MessageType == MessageTypes.JobCanceledInPortal.Key, Ct));
            Assert.Equal(0, await system.IntegrationReceipts.CountAsync(receipt => receipt.TenantId == s.SupplierId, Ct));
        }

        Assert.Equal([SlotAssignmentState.Assigned], await AssignmentScenario.StatesAsync(this.database, Ct, assignment));
        Assert.DoesNotContain(MessageTypes.AssignmentCanceled.Key, await this.NoticesToAsync(s.SupplierId, tess));

        s.Clock.Advance(IntegrationRetryRules.FirstRetry);
        await this.DeliverAsync(s);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Single(await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == jobId && entry.ToState == (int)JobState.CanceledByCustomer).ToListAsync(Ct));
        }

        Assert.Equal([SlotAssignmentState.Canceled], await AssignmentScenario.StatesAsync(this.database, Ct, assignment));
        Assert.Single(await this.NoticesToAsync(s.SupplierId, tess), type => type == MessageTypes.AssignmentCanceled.Key);
        Assert.Single(await this.NoticesToAsync(s.SupplierId, s.Coordinator), type => type == MessageTypes.JobCanceledInPortal.Key);
    }

    [Fact]
    public async Task A_step_that_tells_someone_or_takes_records_along_runs_inside_another_write_only_with_its_after_commit_work()
    {
        var s = await this.ScenarioAsync("Runner Portal Staffing");
        var requestId = await this.SendAsync(s, "Runner job");
        var jobId = await this.JobOfAsync(requestId);
        await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);

        var portal = s.Portal.RuntimeOf(s.OrdererId);
        await using (var host = this.HostOf(s, portal))
        await using (var context = host.Get<IApplicationDbContextFactory<ApplicationDbContext>>().CreateDbContext(portal))
        {
            await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
            {
                var request = await context.Orders.WithWorkflowGraph().AsTracking().SingleAsync(row => row.Id == requestId, Ct);
                await host.Get<WorkflowService>().RunSystemStepInTransactionAsync<OrderState, Order>(context, request, OrderWorkflowAttribute.FollowStepFrom(OrderState.Sent, OrderState.Coordinating), Ct);
                await transaction.CommitAsync(Ct);
            }

            context.ChangeTracker.Clear();
            await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
            {
                var request = await context.Orders.WithWorkflowGraph().AsTracking().SingleAsync(row => row.Id == requestId, Ct);
                var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Get<WorkflowService>().RunSystemStepInTransactionAsync<OrderState, Order>(context, request, OrderWorkflowAttribute.FollowStepFrom(OrderState.Coordinating, OrderState.Confirmed), Ct));
                Assert.Contains("queues notices", thrown.Message, StringComparison.Ordinal);
            }
        }

        Assert.Equal(OrderState.Coordinating, (await this.RequestAsync(requestId)).State);
        Assert.Empty(await this.NoticesOfAsync(s.PortalId));

        var staffing = s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator);
        await using (var host = this.HostOf(s, staffing))
        await using (var context = host.Get<IApplicationDbContextFactory<ApplicationDbContext>>().CreateDbContext(staffing))
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            var job = await context.Jobs.WithWorkflowGraph().AsTracking().SingleAsync(row => row.Id == jobId, Ct);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Get<WorkflowService>().RunSystemStepInTransactionAsync<JobState, Job>(context, job, JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.Coordinating), Ct));
            Assert.Contains("takes child records along", thrown.Message, StringComparison.Ordinal);
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal(JobState.Coordinating, await system.Jobs.AsNoTracking().Where(row => row.Id == jobId).Select(row => row.State).SingleAsync(Ct));
    }

    private async Task<SyncScenario> ScenarioAsync(string name)
    {
        var staffing = await AssignmentScenario.CreateAsync(this.database, name, Ct);
        var root = staffing.StaffMembers.RootId;
        var portal = await PortalScenario.ForCustomerAsync(this.database, staffing.StaffMembers.Onboarded, staffing.Customer, "Brightpath Learning", root, root, Ct);
        var orderer = await portal.AddOrdererAsync(this.database, "Olive", Ct);
        return new SyncScenario(staffing, portal, orderer, new ManualClock(Anchor));
    }

    private sealed record SyncScenario(AssignmentScenario Staffing, PortalScenario Portal, Guid OrdererId, ManualClock Clock)
    {
        public Guid SupplierId => this.Staffing.TenantId;

        public Guid PortalId => this.Portal.PortalTenantId;

        public Guid Coordinator => this.Staffing.Coordinator;
    }

    private ServiceHost HostOf(SyncScenario s, IRuntimeExecutionContext runtime) =>
        ServiceHost.Create(this.database, runtime, services => services.AddSingleton<TimeProvider>(s.Clock));

    private ServiceHost BackgroundHost(SyncScenario s, IReadOnlyList<IInterceptor>? interceptors = null) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(s.Clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        }, interceptors);

    private async Task<IntegrationOutboxPass> PassAsync(SyncScenario s, IReadOnlyList<IInterceptor>? interceptors = null)
    {
        await using var host = this.BackgroundHost(s, interceptors);
        return await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct);
    }

    private async Task DeliverAsync(SyncScenario s, params Guid[] others)
    {
        var senders = others.Append(s.SupplierId).Append(s.PortalId).ToList();
        for (var pass = 0; pass < 3; pass++)
        {
            await this.PassAsync(s);
            await using var system = this.database.CreateSystemContext();
            if (!await system.IntegrationMessages.AnyAsync(message => senders.Contains(message.TenantId) && message.State != IntegrationMessageState.Delivered, Ct))
            {
                return;
            }
        }

        Assert.Fail("The scenario's messages were not all delivered.");
    }

    private async Task ClockPassAsync(SyncScenario s)
    {
        await using var host = this.BackgroundHost(s);
        await host.Get<JobClock>().RunOnceAsync(Ct);
    }

    private async Task<Guid> WriteAsync(SyncScenario s, string name)
    {
        await using var host = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId));
        var service = host.Get<OrderService>();
        var fresh = await service.NewOrderAsync(Ct);
        Assert.True(fresh.IsSuccess, fresh.Error);
        var request = fresh.Value!;
        (request.Name, request.PeriodStart, request.PeriodEnd) = (name, Start, End);
        request.Schedule = JobSchedule.Recurring;
        var created = await service.CreateOrderAsync(request, OrderWorkflowAttribute.SaveDraftStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Guid> SendAsync(SyncScenario s, string name)
    {
        var id = await this.WriteAsync(s, name);
        var sent = await this.RequestStepAsync(s, id, OrderWorkflowAttribute.SendStep);
        Assert.True(sent.IsSuccess, sent.Error);
        return id;
    }

    private async Task<Result> RequestStepAsync(SyncScenario s, Guid id, string step)
    {
        await using var host = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId));
        var service = host.Get<OrderService>();
        var updater = await service.LoadOrderUpdaterAsync(id, Ct);
        return updater.IsSuccess ? await service.UpdateOrderAndTransitionAsync(updater.Value!, step, null, Ct) : Result.Failure(updater.Error);
    }

    private async Task<OrderDetails> PageAsync(SyncScenario s, Guid id)
    {
        await using var host = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId));
        var read = await host.Get<OrderService>().ReadRequestAsync(id, Ct);
        Assert.True(read.IsSuccess, read.Error);
        return read.Value!;
    }

    private async Task<Guid> PrepareAsync(SyncScenario s, Guid jobId)
    {
        await using (var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var jobs = host.Get<JobService>();
            var updater = await jobs.LoadJobUpdaterAsync(jobId, Ct);
            Assert.True(updater.IsSuccess, updater.Error);
            updater.Value!.Entity.TimeSlots.Add(AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
            var saved = await jobs.UpdateJobAndTransitionAsync(updater.Value, JobWorkflowAttribute.SaveAwaitingStep, null, Ct);
            Assert.True(saved.IsSuccess, saved.Error);
        }

        return await this.AddSlotAsync(s, jobId, "Lead");
    }

    private async Task<Guid> AddSlotAsync(SyncScenario s, Guid jobId, string name)
    {
        await using var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator));
        var added = await host.Get<JobService>().Slots.CreateJobSlotAsync(new JobSlot { JobId = jobId, Name = name }, JobSlotWorkflowAttribute.AddStep, Ct);
        Assert.True(added.IsSuccess, added.Error);
        return added.Value;
    }

    private async Task JobStepAsync(SyncScenario s, Guid jobId, string step)
    {
        await using var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator));
        var moved = await AssignmentScenario.StepJobAsync(host.Get<JobService>(), jobId, step, Ct);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private async Task<Guid> AssignAsync(SyncScenario s, Guid slot, Guid staffUser)
    {
        await using var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator));
        var assigned = await host.Get<SlotAssignmentService>().AssignStaffAsync(slot, await AssignmentScenario.ResourceOfAsync(this.database, staffUser, Ct), Ct);
        Assert.True(assigned.IsSuccess, assigned.Error);
        return assigned.Value;
    }

    private async Task<Guid> RequestStaffAsync(SyncScenario s, Guid slot, Guid staffUser)
    {
        await using var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator));
        var requested = await host.Get<SlotAssignmentService>().RequestStaffAsync(slot, await AssignmentScenario.ResourceOfAsync(this.database, staffUser, Ct), Ct);
        Assert.True(requested.IsSuccess, requested.Error);
        return requested.Value;
    }

    private async Task QueueAsync(SyncScenario s, Guid jobLink, JobStatePayload payload)
    {
        await using var context = this.database.CreateContext(s.Staffing.StaffMembers.Onboarded.OwnerRuntime(), s.Clock);
        IntegrationOutbox.Enqueue(context, OrderSync.JobStateKind, s.PortalId, jobLink, OrderSync.WriteJobState(payload));
        await context.SaveChangesAsync(Ct);
    }

    private async Task QueueFromAsync(SyncScenario s, IRuntimeExecutionContext sender, string kind, Guid target, Guid correlation, string payload)
    {
        await using var context = this.database.CreateContext(sender, s.Clock);
        IntegrationOutbox.Enqueue(context, kind, target, correlation, payload);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<Guid> JobOfAsync(Guid requestId)
    {
        var link = (await this.RequestAsync(requestId)).CrossTenantId;
        await using var system = this.database.CreateSystemContext();
        return await system.Jobs.AsNoTracking().Where(job => job.LinkedCrossTenantId == link).Select(job => job.Id).SingleAsync(Ct);
    }

    private async Task<Order> RequestAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Orders.AsNoTracking().SingleAsync(request => request.Id == id, Ct);
    }

    private async Task<List<Staffora.Core.Entities.Workflow.StateHistoryEntry>> HistoryAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == id).OrderBy(entry => entry.Created).ThenBy(entry => entry.FromState).ToListAsync(Ct);
    }

    private async Task<List<OrderStaff>> StaffRowsAsync(Guid requestId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.OrderStaff.AsNoTracking().Where(row => row.OrderId == requestId).OrderBy(row => row.Position).ToListAsync(Ct);
    }

    private async Task<int> MessagesAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.IntegrationMessages.CountAsync(message => message.TenantId == tenantId, Ct);
    }

    private async Task<List<OutgoingMessage>> NoticesOfAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.OutgoingMessages.AsNoTracking().Where(notice => notice.TenantId == tenantId && notice.Category == MessageCategory.Ordering).ToListAsync(Ct);
    }

    private async Task<List<string>> NoticesToAsync(Guid tenantId, Guid userId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StoredMessages.AsNoTracking()
            .Where(stored => stored.TenantId == tenantId && stored.Recipients.Any(recipient => recipient.UserId == userId))
            .OrderBy(stored => stored.Created).ThenBy(stored => stored.Id)
            .Select(stored => stored.MessageType)
            .ToListAsync(Ct);
    }

    private sealed class MarkFault : SaveChangesInterceptor
    {
        private readonly Guid sender;
        private readonly string kind;

        public MarkFault(Guid sender, string kind = OrderSync.JobStateKind)
        {
            this.sender = sender;
            this.kind = kind;
        }

        public int Struck { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (this.Struck == 0 && eventData.Context is { } context
                && context.ChangeTracker.Entries<IntegrationMessage>().Any(entry => entry.State == EntityState.Modified && entry.Entity.State == IntegrationMessageState.Delivered && entry.Entity.Kind == this.kind && entry.Entity.TenantId == this.sender))
            {
                this.Struck++;
                throw new InvalidOperationException("The forced failure at the delivery's mark.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class BeforeRequestLock : DbCommandInterceptor
    {
        private readonly Func<Task> work;

        public BeforeRequestLock(Func<Task> work)
        {
            this.work = work;
        }

        public int Struck { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (this.Struck == 0 && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("[Orders]", StringComparison.Ordinal) && command.CommandText.Contains(nameof(Order.IsInTransition), StringComparison.Ordinal))
            {
                this.Struck++;
                await this.work();
            }

            return result;
        }
    }
}
