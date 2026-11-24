using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Comments;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public sealed partial class OrderSyncTests
{
    [Fact]
    public async Task A_portal_message_appears_once_in_the_jobs_customer_thread_a_staff_member_cannot_read_it_and_the_reply_reaches_the_orderer()
    {
        var s = await this.ScenarioAsync("Thread Staffing");
        var requestId = await this.SendAsync(s, "Biology for year 8");
        var jobId = await this.JobOfAsync(requestId);
        var slot = await this.PrepareAsync(s, jobId);
        await this.JobStepAsync(s, jobId, JobWorkflowAttribute.StartStep);
        await this.DeliverAsync(s);

        var before = await this.MessagesAsync(s.PortalId);
        CommentThread written;
        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            var posted = await orderer.Get<OrderService>().AddOrderCommentAsync(requestId, CommentAudience.Customer, "  Can the staff member bring lab coats?  ", Ct);
            Assert.True(posted.IsSuccess, posted.Error);
            written = posted.Value!;
        }

        Assert.Equal((OrderService.RequestThreadTitle, 1, "Can the staff member bring lab coats?", true), (written.Title, written.TotalCount, written.Comments[0].Text, written.Comments[0].IsOwn));
        Assert.Equal(before + 1, await this.MessagesAsync(s.PortalId));
        await this.DeliverAsync(s);

        var portalComment = Assert.Single(await this.ThreadAsync(nameof(Staffora.Core.Entities.Portal.Order), requestId));
        var copy = Assert.Single(await this.ThreadAsync(nameof(Job), jobId));
        var job = await this.StoredJobAsync(jobId);
        Assert.Equal(
            (CommentAuthorType.Contact, job.ContactId, s.SupplierId, SystemAccount.IdFor(s.SupplierId), (Guid?)null, portalComment.CrossTenantId, "Can the staff member bring lab coats?"),
            (copy.AuthorType, copy.AuthorContactId, copy.TenantId, copy.CreatedById, copy.ImpersonatedById, copy.LinkedCrossTenantId, copy.Text));
        Assert.NotNull(portalComment.CrossTenantId);
        Assert.Null(copy.CrossTenantId);

        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var threads = await coordinator.Get<JobService>().GetJobCommentsAsync(jobId, Ct);
            Assert.True(threads.IsSuccess, threads.Error);
            Assert.Equal([CommentAudience.Internal, CommentAudience.Customer], threads.Value!.Select(thread => thread.Audience));
            var customer = threads.Value!.Single(thread => thread.Audience == CommentAudience.Customer);
            Assert.True(customer.RequiresAttention);
            Assert.Equal(CommentTurn.AwaitsYourReply, customer.AttentionText);
            Assert.EndsWith(", Brightpath Learning", customer.Comments.Single().Author, StringComparison.Ordinal);
        }

        Assert.Equal([MessageTypes.JobCustomerWrote.Key], await this.ConversationNoticesToAsync(s.SupplierId, s.Coordinator));
        var told = Assert.Single(await this.ConversationNoticesOfAsync(s.SupplierId));
        Assert.Equal([s.Coordinator], told.RecipientUserIds);
        Assert.Equal(((RolePermission?)RolePermission.ViewCoordination, (RolePermission?)null, (RolePermission?)RolePermission.PerformCoordination, $"?dlg=job-{jobId}"), (told.NamedRecipientsHold, told.RecipientPermission, told.FallbackPermission, told.Link));
        Assert.DoesNotContain("lab coats", told.Text, StringComparison.Ordinal);

        var link = (await this.RequestAsync(requestId)).CrossTenantId!.Value;
        var writer = await this.WriterAsync(s.OrdererId);
        await this.QueueFromAsync(s, s.Portal.RuntimeOf(s.OrdererId), JobThreadSync.ToSupplierKind, s.SupplierId, link, JobThreadSync.WriteToSupplier(new ToSupplierComment(portalComment.CrossTenantId!.Value, portalComment.Text, writer))!);
        await this.DeliverAsync(s);
        Assert.Single(await this.ThreadAsync(nameof(Job), jobId));
        Assert.Single(await this.ConversationNoticesOfAsync(s.SupplierId));

        var staff = await s.Staffing.AddStaffAsync(this.database, "Theo", Ct);
        await this.AssignAsync(s, slot, staff);
        await using (var teaching = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(staff)))
        {
            var read = await teaching.Get<JobService>().GetJobCommentsAsync(jobId, Ct);
            Assert.True(!read.IsSuccess || read.Value!.All(thread => thread.Audience != CommentAudience.Customer));
            Assert.False((await teaching.Get<JobService>().AddJobCommentAsync(jobId, CommentAudience.Customer, "Hello", Ct)).IsSuccess);
            var direct = await teaching.Get<CommentService>().GetThreadsAsync<Job>(jobId, [JobService.CustomerThread], Ct);
            Assert.True(direct.IsSuccess, direct.Error);
            Assert.Empty(direct.Value!);
        }

        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var replied = await coordinator.Get<JobService>().AddJobCommentAsync(jobId, CommentAudience.Customer, "Yes, she will.", Ct);
            Assert.True(replied.IsSuccess, replied.Error);
            Assert.False(replied.Value!.RequiresAttention);
        }

        await this.DeliverAsync(s);
        var portalThread = await this.ThreadAsync(nameof(Staffora.Core.Entities.Portal.Order), requestId);
        Assert.Equal(2, portalThread.Count);
        var reply = portalThread[1];
        var party = await this.SupplierPartyAsync(s.PortalId);
        Assert.Equal(
            (CommentAuthorType.Supplier, (Guid?)party, s.PortalId, SystemAccount.IdFor(s.PortalId), (Guid?)null, (Guid?)null, "Yes, she will."),
            (reply.AuthorType, reply.AuthorSupplierId, reply.TenantId, reply.CreatedById, reply.ImpersonatedById, reply.AuthorContactId, reply.Text));
        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            var threads = await orderer.Get<OrderService>().GetOrderCommentsAsync(requestId, Ct);
            var messages = Assert.Single(threads.Value!);
            Assert.Equal((OrderService.RequestThreadTitle, true), (messages.Title, messages.RequiresAttention));
            Assert.Equal(await this.TenantNameAsync(s.SupplierId), messages.Comments[1].Author);
        }

        Assert.Equal([MessageTypes.OrderSupplierWrote.Key], await this.ConversationNoticesToAsync(s.PortalId, s.OrdererId));
    }

    [Fact]
    public async Task A_message_on_a_job_without_a_coordinator_tells_every_coordinator_and_closed_or_draft_threads_take_none()
    {
        var s = await this.ScenarioAsync("Thread Fallback");
        var requestId = await this.SendAsync(s, "Chemistry for year 9");
        var jobId = await this.JobOfAsync(requestId);

        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            Assert.True((await orderer.Get<OrderService>().AddOrderCommentAsync(requestId, CommentAudience.Customer, "Any news?", Ct)).IsSuccess);
        }

        await this.DeliverAsync(s);
        var told = Assert.Single(await this.ConversationNoticesOfAsync(s.SupplierId));
        Assert.Equal((0, (RolePermission?)null, (RolePermission?)RolePermission.PerformCoordination), (told.RecipientUserIds.Count, told.RecipientPermission, told.FallbackPermission));
        Assert.Equal([MessageTypes.JobCustomerWrote.Key], await this.ConversationNoticesToAsync(s.SupplierId, s.Coordinator));

        var draft = await this.WriteAsync(s, "Draft job");
        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            var requests = orderer.Get<OrderService>();
            Assert.Empty((await requests.GetOrderCommentsAsync(draft, Ct)).Value!);
            Assert.Equal(OrderService.RequestThreadNotSent, (await requests.AddOrderCommentAsync(draft, CommentAudience.Customer, "Hello", Ct)).Error);
        }

        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var jobs = coordinator.Get<JobService>();
            var cancel = JobWorkflowAttribute.CancelStepFrom(JobState.AwaitingCoordination);
            var updater = await jobs.LoadJobUpdaterAsync(jobId, Ct);
            Assert.False((await jobs.UpdateJobAndTransitionAsync(updater.Value!, cancel, null, Ct)).IsSuccess);
            Assert.Equal(JobState.AwaitingCoordination, (await this.StoredJobAsync(jobId)).State);
            Assert.True((await AssignmentScenario.StepJobAsync(jobs, jobId, cancel, Ct, "The customer's budget was cut.")).IsSuccess);
            var threads = (await jobs.GetJobCommentsAsync(jobId, Ct)).Value!;
            var note = Assert.Single(threads.Single(thread => thread.Audience == CommentAudience.Internal).Comments);
            Assert.Equal((CommentKind.TransitionNote, "The customer's budget was cut."), (note.Kind, note.Text));

            Assert.False(threads.Single(thread => thread.Audience == CommentAudience.Customer).CanPost);
            Assert.Equal(JobService.CustomerThreadClosed, (await jobs.AddJobCommentAsync(jobId, CommentAudience.Customer, "Sorry.", Ct)).Error);
        }

        var (staffJob, _) = await s.Staffing.StartedJobAsync(this.database, "Staff job", Start, End, Ct);
        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var threads = await coordinator.Get<JobService>().GetJobCommentsAsync(staffJob, Ct);
            Assert.Equal([CommentAudience.Internal], threads.Value!.Select(thread => thread.Audience));
            Assert.Equal(CommentService.NoAccess, (await coordinator.Get<JobService>().AddJobCommentAsync(staffJob, CommentAudience.Customer, "Hello", Ct)).Error);
        }
    }

    [Fact]
    public async Task A_colleagues_message_never_awaits_the_readers_reply_and_a_reply_for_an_orderer_who_left_tells_the_customer_administrators()
    {
        var s = await this.ScenarioAsync("Thread Sides");
        var requestId = await this.SendAsync(s, "Physics for year 7");
        var jobId = await this.JobOfAsync(requestId);
        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            Assert.True((await orderer.Get<OrderService>().AddOrderCommentAsync(requestId, CommentAudience.Customer, "Is there a lab?", Ct)).IsSuccess);
        }

        await using (var admin = this.HostOf(s, s.Portal.AdministratorRuntime()))
        {
            var messages = Assert.Single((await admin.Get<OrderService>().GetOrderCommentsAsync(requestId, Ct)).Value!);
            Assert.False(messages.RequiresAttention);
        }

        await this.DeliverAsync(s);

        s.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var jobs = coordinator.Get<JobService>();
            Assert.True((await jobs.GetJobCommentsAsync(jobId, Ct)).Value!.Single(thread => thread.Audience == CommentAudience.Customer).RequiresAttention);
            var replied = await jobs.AddJobCommentAsync(jobId, CommentAudience.Customer, "Yes, room 4.", Ct);
            Assert.True(replied.IsSuccess, replied.Error);
            Assert.False(replied.Value!.RequiresAttention);
        }

        await using (var owner = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var customer = (await owner.Get<JobService>().GetJobCommentsAsync(jobId, Ct)).Value!.Single(thread => thread.Audience == CommentAudience.Customer);
            Assert.Equal((2, false), (customer.TotalCount, customer.RequiresAttention));
        }

        await using (var admin = this.HostOf(s, s.Portal.AdministratorRuntime()))
        {
            var left = await admin.Get<Staffora.Services.Services.UserService>().StepUserAsync(s.OrdererId, ApplicationUserWorkflowAttribute.PortalDeactivateStep);
            Assert.True(left.IsSuccess, left.Error);
        }

        await this.DeliverAsync(s);
        var told = Assert.Single(await this.ConversationNoticesOfAsync(s.PortalId));
        Assert.Equal([s.OrdererId], told.RecipientUserIds);
        Assert.Equal(((RolePermission?)RolePermission.OrdersView, (RolePermission?)RolePermission.CustomerUsersManage), (told.NamedRecipientsHold, told.FallbackPermission));
        Assert.Equal([MessageTypes.OrderSupplierWrote.Key], await this.ConversationNoticesToAsync(s.PortalId, s.Portal.AdministratorId));
        Assert.Empty(await this.ConversationNoticesToAsync(s.PortalId, s.OrdererId));
        await using (var admin = this.HostOf(s, s.Portal.AdministratorRuntime()))
        {
            var messages = Assert.Single((await admin.Get<OrderService>().GetOrderCommentsAsync(requestId, Ct)).Value!);
            Assert.True(messages.RequiresAttention);
            Assert.Equal(messages.Comments[1].Id, messages.FirstMarkedCommentId);
        }
    }

    private async Task<List<Comment>> ThreadAsync(string entityType, Guid entityId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Comments.AsNoTracking()
            .Where(comment => comment.CommentContainer!.EntityType == entityType && comment.CommentContainer.EntityId == entityId && comment.CommentContainer.Audience == CommentAudience.Customer)
            .OrderBy(comment => comment.Created).ThenBy(comment => comment.Id)
            .ToListAsync(Ct);
    }

    private async Task<Job> StoredJobAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Jobs.AsNoTracking().SingleAsync(job => job.Id == id, Ct);
    }

    private async Task<Guid> SupplierPartyAsync(Guid portalId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Suppliers.AsNoTracking().Where(supplier => supplier.TenantId == portalId).Select(supplier => supplier.Id).SingleAsync(Ct);
    }

    private async Task<string> TenantNameAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.Name).SingleAsync(Ct);
    }

    private async Task<CommentWriter> WriterAsync(Guid userId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => new CommentWriter(user.Id, user.FirstName, user.LastName, user.Email)).SingleAsync(Ct);
    }

    private async Task<List<OutgoingMessage>> ConversationNoticesOfAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.OutgoingMessages.AsNoTracking().Where(notice => notice.TenantId == tenantId && notice.Category == MessageCategory.Conversation).OrderBy(notice => notice.Created).ToListAsync(Ct);
    }

    private async Task<List<string>> ConversationNoticesToAsync(Guid tenantId, Guid userId) =>
        [.. (await this.NoticesToAsync(tenantId, userId)).Where(kind => kind == MessageTypes.JobCustomerWrote.Key || kind == MessageTypes.OrderSupplierWrote.Key)];
}
