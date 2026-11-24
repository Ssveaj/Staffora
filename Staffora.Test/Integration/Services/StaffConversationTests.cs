using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Comments;
using Staffora.Services.Coordination;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Messaging;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class StaffConversationTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public StaffConversationTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_staff_members_question_raises_attention_on_the_job_and_tells_the_coordinator_and_the_reply_clears_it_and_tells_the_staff_member()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Done When", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead"]);
        var coordinator = scenario.Coordinator;
        var assignment = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));
        var noticesBefore = (await scenario.NoticesOfAsync(this.database, coordinator, Ct)).Count;

        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            var posted = await teaching.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Is the room on the ground floor?", Ct);
            Assert.True(posted.IsSuccess, posted.Error);
            Assert.False(posted.Value!.RequiresAttention);
        }

        await using (var coordinating = scenario.HostOf(this.database, coordinator))
        {
            var row = Assert.Single((await coordinating.Get<JobService>().Conversations.GetJobConversationsAsync(job, Ct)).Value!);
            Assert.Equal((assignment, "Ada Example", true, 1, "Is the room on the ground floor?"), (row.AssignmentId, row.Staff, row.AwaitsReply, row.Count, row.LastText));
            var thread = Assert.Single((await coordinating.Get<JobService>().Conversations.GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!);
            Assert.True(thread.RequiresAttention);
            Assert.Equal(CommentTurn.AwaitsYourReply, thread.AttentionText);
            Assert.Equal(thread.Comments[^1].Id, thread.FirstMarkedCommentId);
        }

        var told = await scenario.NoticesOfAsync(this.database, coordinator, Ct);
        Assert.Equal(noticesBefore + 1, told.Count);
        Assert.Equal(("Ada Example wrote about Algebra", $"?dlg=assignment-{assignment}"), (told[0].Subject, told[0].Link));
        Assert.DoesNotContain("ground floor", told[0].Text, StringComparison.Ordinal);

        var adaBefore = (await scenario.NoticesOfAsync(this.database, ada, Ct)).Count;
        await using (var coordinating = scenario.HostOf(this.database, coordinator))
        {
            var conversations = coordinating.Get<JobService>().Conversations;
            Assert.True((await conversations.AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Yes, room 4.", Ct)).IsSuccess);
            var row = Assert.Single((await conversations.GetJobConversationsAsync(job, Ct)).Value!);
            Assert.False(row.AwaitsReply);
            Assert.False(Assert.Single((await conversations.GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!).RequiresAttention);
        }

        var adaTold = await scenario.NoticesOfAsync(this.database, ada, Ct);
        Assert.Equal(adaBefore + 1, adaTold.Count);
        Assert.EndsWith(" wrote about Algebra", adaTold[0].Subject, StringComparison.Ordinal);
        Assert.Equal($"?dlg=assignment-{assignment}", adaTold[0].Link);
        Assert.DoesNotContain("room 4", adaTold[0].Text, StringComparison.Ordinal);
        Assert.Equal(noticesBefore + 1, (await scenario.NoticesOfAsync(this.database, coordinator, Ct)).Count);
    }

    [Fact]
    public async Task The_staff_member_sees_a_new_reply_on_my_assignments_and_in_the_thread_until_they_read_it()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Unseen Reply", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using (var coordinating = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Assert.True((await coordinating.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Can you start a week early?", Ct)).IsSuccess);
        }

        var impersonating = scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(ada, scenario.TenantId);
        await using (var host = ServiceHost.Create(this.database, impersonating, AssignmentScenario.UseClock))
        {
            var thread = Assert.Single((await host.Get<StaffConversationService>().GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!);
            Assert.Equal((true, CommentTurn.NewReply), (thread.RequiresAttention, thread.AttentionText));
        }

        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            var service = teaching.Get<SlotAssignmentService>();
            Assert.True(Assert.Single((await service.QueryOwnAssignmentsAsync(new ListQuery(), Ct)).Value!.Rows).HasUnseenReply);

            var thread = Assert.Single((await service.Conversations.GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!);
            Assert.True(thread.RequiresAttention);
            Assert.Equal(("New", thread.Comments[^1].Id), (thread.Turn!.MarkerLabel, thread.FirstMarkedCommentId));

            Assert.False(Assert.Single((await service.QueryOwnAssignmentsAsync(new ListQuery(), Ct)).Value!.Rows).HasUnseenReply);
            Assert.False(Assert.Single((await service.Conversations.GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!).RequiresAttention);
        }
    }

    [Fact]
    public async Task Whose_turn_follows_the_last_post_on_a_clock_that_does_not_move_and_two_posts_at_once_both_land_in_one_order()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Turn Race", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Chemistry", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        var still = new ManualClock(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
        void UseStill(IServiceCollection services) => services.AddSingleton<TimeProvider>(still);
        await using var teaching = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(ada), UseStill);
        await using var coordinating = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator), UseStill);
        var staff = teaching.Get<StaffConversationService>();
        var coordinatorSide = coordinating.Get<StaffConversationService>();
        foreach (var (service, awaits) in new[] { (staff, true), (coordinatorSide, false), (staff, true), (staff, true), (coordinatorSide, false) })
        {
            Assert.True((await service.AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Same instant", Ct)).IsSuccess);
            Assert.Equal(awaits, Assert.Single((await coordinatorSide.GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!).RequiresAttention);
        }

        for (var round = 0; round < 8; round++)
        {
            await using var adaHost = scenario.HostOf(this.database, ada);
            await using var coordinatorHost = scenario.HostOf(this.database, scenario.Coordinator);
            var posts = await Task.WhenAll(
                adaHost.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, $"Staff {round}", Ct),
                coordinatorHost.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, $"Coordinator {round}", Ct));
            Assert.All(posts, post => Assert.True(post.IsSuccess, post.Error));

            var stored = await AssignmentScenario.StaffThreadAsync(this.database, assignment, Ct);
            var stamps = await AssignmentScenario.StoredAsync(this.database, assignment, Ct);
            Assert.Equal(stored[^1].StartsWith("Staff", StringComparison.Ordinal), stamps!.LastStaffCommentAt > stamps.LastCoordinatorCommentAt);
        }

        Assert.Equal(5 + 16, (await AssignmentScenario.StaffThreadAsync(this.database, assignment, Ct)).Count);
    }

    [Fact]
    public async Task Only_the_staff_member_and_the_coordinators_read_a_staff_members_conversation_by_any_path()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Visibility", Ct);
        var other = await AssignmentScenario.CreateAsync(this.database, "Other Conversations", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var adaAssignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        _ = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[1], bo, Ct));
        var boBefore = (await scenario.NoticesOfAsync(this.database, bo, Ct)).Count;
        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            Assert.True((await teaching.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Private question", Ct)).IsSuccess);
        }

        await using (var bos = scenario.HostOf(this.database, bo))
        {
            var assignments = bos.Get<SlotAssignmentService>();
            Assert.Equal(SlotAssignmentService.NotFound, (await assignments.Conversations.GetSlotAssignmentCommentsAsync(adaAssignment, Ct)).Error);
            Assert.Equal(SlotAssignmentService.NotFound, (await assignments.Conversations.AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Hello", Ct)).Error);
            Assert.Equal(CommentService.NoAccess, (await assignments.Conversations.GetJobConversationsAsync(job, Ct)).Error);
            var throughTheJob = bos.Get<JobService>().Conversations;
            Assert.Equal(CommentService.NoAccess, (await throughTheJob.GetJobConversationsAsync(job, Ct)).Error);
            Assert.Equal(SlotAssignmentService.NotFound, (await throughTheJob.GetSlotAssignmentCommentsAsync(adaAssignment, Ct)).Error);
            Assert.Equal(SlotAssignmentService.NotFound, (await throughTheJob.AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Hello", Ct)).Error);
            Assert.False(Assert.Single((await assignments.QueryOwnAssignmentsAsync(new ListQuery(), Ct)).Value!.Rows).HasUnseenReply);
        }

        Assert.Equal(boBefore, (await scenario.NoticesOfAsync(this.database, bo, Ct)).Count);
        Assert.Equal(["Private question"], await AssignmentScenario.StaffThreadAsync(this.database, adaAssignment, Ct));

        var cy = await scenario.AddStaffAsync(this.database, "Cy", Ct);
        await using (var cys = scenario.HostOf(this.database, cy))
        {
            Assert.Equal(SlotAssignmentService.NotFound, (await cys.Get<StaffConversationService>().GetSlotAssignmentCommentsAsync(adaAssignment, Ct)).Error);
        }

        var registrar = await scenario.StaffMembers.AddPersonAsync(this.database, "Reg", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, registrar, Ct, RolePermission.JobsView);
        await using (var reading = scenario.HostOf(this.database, registrar))
        {
            Assert.Empty((await reading.Get<StaffConversationService>().GetSlotAssignmentCommentsAsync(adaAssignment, Ct)).Value!);
            Assert.Equal(CommentService.NoAccess, (await reading.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Hello", Ct)).Error);
            Assert.Equal(CommentService.NoAccess, (await reading.Get<JobService>().Conversations.GetJobConversationsAsync(job, Ct)).Error);
            Assert.Empty((await reading.Get<JobService>().GetJobCommentsAsync(job, Ct)).Value!);
        }

        await using (var stranger = other.HostOf(this.database, other.Coordinator))
        {
            Assert.Equal(SlotAssignmentService.NotFound, (await stranger.Get<StaffConversationService>().GetSlotAssignmentCommentsAsync(adaAssignment, Ct)).Error);
            Assert.Equal(SlotAssignmentService.NotFound, (await stranger.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(adaAssignment, CommentAudience.Staff, "Hello", Ct)).Error);
            Assert.Empty((await stranger.Get<JobService>().Conversations.GetJobConversationsAsync(job, Ct)).Value!);
        }

        Assert.Equal(["Private question"], await AssignmentScenario.StaffThreadAsync(this.database, adaAssignment, Ct));
        var stamps = await AssignmentScenario.StoredAsync(this.database, adaAssignment, Ct);
        Assert.Null(stamps!.LastCoordinatorCommentAt);
        Assert.NotNull(stamps.LastStaffCommentAt);
    }

    [Fact]
    public async Task An_impersonated_staff_members_post_is_the_staff_members_and_a_post_tells_only_those_who_may_still_read_it()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Recipients", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var dana = await scenario.StaffMembers.AddPersonAsync(this.database, "Dana", Ct);
        var roles = scenario.StaffMembers.Onboarded.Roles;
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(dana, scenario.StaffMembers.RootId, roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        var (_, slots) = await scenario.StartedJobAsync(this.database, "History", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, dana, slots[0], ada, Ct));
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var holding = await StaffScenario.HoldingIdAsync(this.database, dana, roles.Coordinator, scenario.StaffMembers.RootId, Ct);
            Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
        }

        var coordinatorBefore = (await scenario.NoticesOfAsync(this.database, scenario.Coordinator, Ct)).Count;
        var impersonating = scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(ada, scenario.TenantId);
        await using (var host = ServiceHost.Create(this.database, impersonating, AssignmentScenario.UseClock))
        {
            Assert.True((await host.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Asked for Ada", Ct)).IsSuccess);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            var comment = await context.Comments.AsNoTracking().SingleAsync(row => row.CommentContainer!.EntityId == assignment, Ct);
            Assert.Equal((ada, (Guid?)scenario.StaffMembers.Onboarded.Platform.OwnerId), (comment.CreatedById, comment.ImpersonatedById));
            var stored = await context.SlotAssignments.AsNoTracking().SingleAsync(row => row.Id == assignment, Ct);
            Assert.NotNull(stored.LastStaffCommentAt);
            Assert.Null(stored.StaffSeenAt);
            var notice = await context.OutgoingMessages.AsNoTracking().SingleAsync(row => row.EntityId == assignment && row.MessageType == MessageTypes.AssignmentStaffWrote.Key, Ct);
            Assert.Equal((comment.Id, RolePermission.ViewCoordination), (notice.HistoryEntryId, notice.NamedRecipientsHold));
        }

        Assert.Equal(coordinatorBefore + 1, (await scenario.NoticesOfAsync(this.database, scenario.Coordinator, Ct)).Count);
        Assert.Empty(await scenario.NoticesOfAsync(this.database, dana, Ct));
    }

    [Fact]
    public async Task A_post_publishes_its_job_and_staff_member_after_its_commit_and_a_refused_post_stamps_tells_and_publishes_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Changes", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Latin", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, assignment, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, comment: "Busy that period")).IsSuccess);
        var resource = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);

        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            var heard = new List<TenantChange>();
            using var subscription = teaching.Get<TenantChangeBus>().Subscribe(scenario.TenantId, change => { lock (heard) { heard.Add(change); } return Task.CompletedTask; });
            Assert.Equal(StaffConversationService.ThreadClosedForStaff, (await teaching.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Actually…", Ct)).Error);
            Assert.Empty(heard);
        }

        var stored = await AssignmentScenario.StoredAsync(this.database, assignment, Ct);
        Assert.Equal((null, null), (stored!.LastStaffCommentAt, stored.LastCoordinatorCommentAt));

        await using (var coordinating = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var heard = new TaskCompletionSource<TenantChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = coordinating.Get<TenantChangeBus>().Subscribe(scenario.TenantId, change => { heard.TrySetResult(change); return Task.CompletedTask; });
            Assert.True((await coordinating.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Understood, thanks", Ct)).IsSuccess);
            var change = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal([job], change.JobIds);
            Assert.Equal([resource], change.StaffIds);
        }

        stored = await AssignmentScenario.StoredAsync(this.database, assignment, Ct);
        Assert.Null(stored!.LastStaffCommentAt);
        Assert.NotNull(stored.LastCoordinatorCommentAt);
    }

    [Fact]
    public async Task A_coordinators_note_on_the_job_is_refused_to_a_staff_member_and_is_never_notified_or_published()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Internal Notes", Ct);
        var other = await AssignmentScenario.CreateAsync(this.database, "Other Notes", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Geometry", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        var adaBefore = (await scenario.NoticesOfAsync(this.database, ada, Ct)).Count;
        int outgoingBefore;
        await using (var context = this.database.CreateSystemContext())
        {
            outgoingBefore = await context.OutgoingMessages.CountAsync(row => row.TenantId == scenario.TenantId, Ct);
        }

        await using (var coordinating = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var heard = new List<TenantChange>();
            using var subscription = coordinating.Get<TenantChangeBus>().Subscribe(scenario.TenantId, change => { lock (heard) { heard.Add(change); } return Task.CompletedTask; });
            var jobs = coordinating.Get<JobService>();
            var posted = await jobs.AddJobCommentAsync(job, CommentAudience.Internal, "Ada's contract ends in May", Ct);
            Assert.True(posted.IsSuccess, posted.Error);
            Assert.Equal(CommentAudience.Internal, posted.Value!.Audience);
            Assert.Equal(["Ada's contract ends in May"], Assert.Single((await jobs.GetJobCommentsAsync(job, Ct)).Value!).Comments.Select(comment => comment.Text));
            Assert.Equal(CommentService.NoAccess, (await jobs.AddJobCommentAsync(job, CommentAudience.Staff, "Wrong thread", Ct)).Error);
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
            Assert.Empty(heard);
        }

        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            Assert.Equal(JobService.NoView, (await teaching.Get<JobService>().GetJobCommentsAsync(job, Ct)).Error);
            Assert.Equal(JobService.NoView, (await teaching.Get<JobService>().AddJobCommentAsync(job, CommentAudience.Internal, "Let me in", Ct)).Error);
            Assert.Empty(Assert.Single((await teaching.Get<StaffConversationService>().GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!).Comments);
            Assert.Equal(CommentService.NoAccess, await NoteRefusalPastTheJobServiceAsync(teaching, job));
        }

        await using (var stranger = other.HostOf(this.database, other.Coordinator))
        {
            Assert.Equal("The Job was not found.", (await stranger.Get<JobService>().GetJobCommentsAsync(job, Ct)).Error);
            Assert.Equal("The Job was not found.", (await stranger.Get<JobService>().AddJobCommentAsync(job, CommentAudience.Internal, "Peek", Ct)).Error);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            Assert.Equal(outgoingBefore, await context.OutgoingMessages.CountAsync(row => row.TenantId == scenario.TenantId, Ct));
        }

        Assert.Equal(adaBefore, (await scenario.NoticesOfAsync(this.database, ada, Ct)).Count);
    }

    [Fact]
    public async Task A_post_reads_no_other_row_while_it_holds_its_assignment_so_a_job_step_holding_the_job_never_deadlocks_with_it()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Lock Order", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Music", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        await using var threads = new SqlConnection(this.database.ConnectionString);
        await threads.OpenAsync(Ct);
        var holding = (SqlTransaction)await threads.BeginTransactionAsync(Ct);
        await using (var take = new SqlCommand("EXEC sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction'", threads, holding))
        {
            take.Parameters.AddWithValue("@resource", AppLock.CommentThread.Resource(scenario.TenantId, CommentThreadWriter.ThreadOf(nameof(SlotAssignment), assignment)));
            await take.ExecuteNonQueryAsync(Ct);
        }

        await using var teaching = scenario.HostOf(this.database, ada);
        var post = teaching.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Posted while the job moves", Ct);
        await WaitForApplicationLockWaiterAsync();

        await using var step = new SqlConnection(this.database.ConnectionString);
        await step.OpenAsync(Ct);
        var stepping = (SqlTransaction)await step.BeginTransactionAsync(Ct);
        await ExecuteAsync(step, stepping, "UPDATE Jobs SET IsInTransition = IsInTransition WHERE Id = @id", job);
        await holding.RollbackAsync(Ct);
        var posted = await post.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.True(posted.IsSuccess, posted.Error);
        await ExecuteAsync(step, stepping, "UPDATE SlotAssignments SET IsInTransition = IsInTransition WHERE Id = @id", assignment);
        await stepping.RollbackAsync(Ct);

        Assert.Contains(await scenario.NoticesOfAsync(this.database, scenario.Coordinator, Ct), notice => notice.Subject == "Ada Example wrote about Music");
    }

    [Fact]
    public async Task A_post_that_fails_as_it_saves_leaves_no_comment_no_stamp_and_no_notice()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Rollback", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Drama", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        int outgoingBefore;
        await using (var context = this.database.CreateSystemContext())
        {
            outgoingBefore = await context.OutgoingMessages.CountAsync(row => row.TenantId == scenario.TenantId, Ct);
        }

        await using (var coordinating = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var access = new CommentThreadAccess(CommentAudience.Staff, RolePermission.ViewCoordination, RolePermission.PerformCoordination)
            {
                Effects = new CommentPostEffects(
                    async (context, now, token) => await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE SlotAssignments SET LastCoordinatorCommentAt = {now} WHERE Id = {assignment}", token) > 0,
                    (context, _, _) => Task.FromResult(new CommentPostOutcome(
                        [new OutgoingMessage
                        {
                            TenantId = scenario.TenantId,
                            MessageType = MessageTypes.AssignmentCoordinatorWrote.Key,
                            Category = MessageTypes.AssignmentCoordinatorWrote.Category,
                            EntityType = nameof(SlotAssignment),
                            EntityId = assignment,
                            Subject = new string('x', MessageCatalogue.MaxSubjectLength + 1),
                            Text = "Refused",
                            RecipientUserIds = [ada],
                        }],
                        null))),
            };
            var posted = await coordinating.Get<CommentService>().AddCommentAsync<SlotAssignment>(assignment, access, "Never stored", Ct);
            Assert.False(posted.IsSuccess);
        }

        Assert.Empty(await AssignmentScenario.StaffThreadAsync(this.database, assignment, Ct));
        Assert.Null((await AssignmentScenario.StoredAsync(this.database, assignment, Ct))!.LastCoordinatorCommentAt);
        await using (var context = this.database.CreateSystemContext())
        {
            Assert.Equal(outgoingBefore, await context.OutgoingMessages.CountAsync(row => row.TenantId == scenario.TenantId, Ct));
        }
    }

    [Fact]
    public async Task A_posts_notices_are_handed_to_the_dispatcher_only_once_the_comment_is_committed()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Dispatch", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Poetry", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        var probe = new CommentCommitProbe(this.database);
        await using (var teaching = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(ada), services =>
        {
            AssignmentScenario.UseClock(services);
            services.AddSingleton<IOutgoingMessageDispatcher>(probe);
        }))
        {
            Assert.True((await teaching.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, "Is it committed?", Ct)).IsSuccess);
        }

        var (messages, commentId, committed) = Assert.Single(probe.Batches);
        Assert.Equal([MessageTypes.AssignmentStaffWrote.Key], messages.Select(message => message.MessageType));
        Assert.True(committed);
        await using var context = this.database.CreateSystemContext();
        Assert.Equal("Is it committed?", await context.Comments.AsNoTracking().Where(comment => comment.Id == commentId).Select(comment => comment.Text).SingleAsync(Ct));
    }

    [Fact]
    public async Task Another_coordinators_reply_is_not_marked_unanswered_and_a_reply_after_the_staff_members_read_is_new_on_a_standing_clock()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Conversation Markers", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var dana = await scenario.StaffMembers.AddPersonAsync(this.database, "Dana", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(dana, scenario.StaffMembers.RootId, scenario.StaffMembers.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        var (_, slots) = await scenario.StartedJobAsync(this.database, "Statistics", PeriodStart, PeriodEnd, Ct);
        var assignment = Made(await scenario.AssignAsync(this.database, dana, slots[0], ada, Ct));
        foreach (var (who, text) in new[] { (ada, "First question"), (dana, "Dana answers"), (ada, "Second question") })
        {
            await using var host = scenario.HostOf(this.database, who);
            Assert.True((await host.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assignment, CommentAudience.Staff, text, Ct)).IsSuccess);
        }

        await using (var coordinating = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var thread = Assert.Single((await coordinating.Get<StaffConversationService>().GetSlotAssignmentCommentsAsync(assignment, Ct)).Value!);
            Assert.True(thread.RequiresAttention);
            Assert.Equal("Second question", thread.Comments.Single(comment => comment.Id == thread.FirstMarkedCommentId).Text);
        }

        var still = new ManualClock(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
        void UseStill(IServiceCollection services) => services.AddSingleton<TimeProvider>(still);
        var other = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, (await scenario.StartedJobAsync(this.database, "Logic", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(10, 0)))).Slots[0], ada, Ct));
        await using var teaching = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(ada), UseStill);
        await using var staffing = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator), UseStill);
        var staff = teaching.Get<SlotAssignmentService>();
        var coordinatorSide = staffing.Get<SlotAssignmentService>();
        Assert.True((await staff.Conversations.AddSlotAssignmentCommentAsync(other, CommentAudience.Staff, "Same instant", Ct)).IsSuccess);
        Assert.True((await coordinatorSide.Conversations.AddSlotAssignmentCommentAsync(other, CommentAudience.Staff, "First reply", Ct)).IsSuccess);
        Assert.True(Assert.Single((await staff.Conversations.GetSlotAssignmentCommentsAsync(other, Ct)).Value!).RequiresAttention);
        Assert.False(Assert.Single((await staff.Conversations.GetSlotAssignmentCommentsAsync(other, Ct)).Value!).RequiresAttention);
        Assert.True((await coordinatorSide.Conversations.AddSlotAssignmentCommentAsync(other, CommentAudience.Staff, "Second reply", Ct)).IsSuccess);
        Assert.True(Assert.Single((await staff.QueryOwnAssignmentsAsync(new ListQuery(), Ct)).Value!.Rows, row => row.Id == other).HasUnseenReply);
        var reread = Assert.Single((await staff.Conversations.GetSlotAssignmentCommentsAsync(other, Ct)).Value!);
        Assert.Equal((true, CommentTurn.NewReply), (reread.RequiresAttention, reread.AttentionText));
    }

    [Fact]
    public async Task A_coordinator_marks_a_staff_members_message_handled_without_a_reply_and_it_settles_for_every_coordinator()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Message Handled", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var dana = await scenario.AddCoordinatorAsync(this.database, "Dana", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Sculpture", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var casey = scenario.Coordinator;
        var assigned = Made(await scenario.AssignAsync(this.database, dana, slots[0], ada, Ct));
        var accepted = Made(await scenario.RequestAsync(this.database, dana, slots[1], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, accepted, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        var coordinators = new[] { casey, dana };

        Assert.Contains(SlotAssignmentWorkflowAttribute.AwaitsReplyReason, (await scenario.StepAsync(this.database, casey, assigned, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep, Ct)).Error, StringComparison.Ordinal);
        foreach (var (staff, id) in new[] { (ada, assigned), (bo, accepted) })
        {
            await using var writing = scenario.HostOf(this.database, staff);
            Assert.True((await writing.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(id, CommentAudience.Staff, "Is there a parking card?", Ct)).IsSuccess);
        }

        foreach (var coordinator in coordinators)
        {
            Assert.Equal((1, 1), (await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, assigned, Ct), await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, accepted, Ct)));
            var keys = await scenario.Absence.StaffQuestionKeysAsync(this.database, coordinator, Ct);
            Assert.Contains($"message-{assigned}", keys);
            Assert.Contains($"message-{accepted}", keys);
        }

        Assert.False((await scenario.StepAsync(this.database, ada, assigned, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep, Ct)).IsSuccess);
        Assert.Equal(SlotAssignmentService.NotFound, (await scenario.StepAsync(this.database, bo, assigned, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep, Ct)).Error);
        await using (var coordinating = scenario.HostOf(this.database, casey))
        {
            var offered = await coordinating.Get<Staffora.Services.Workflow.WorkflowService>().GetAvailableTransitionsAsync<SlotAssignmentState, SlotAssignment>(assigned, SlotAssignmentWorkflowQueries.WithWorkflowGraph, Ct);
            Assert.True(offered.IsSuccess, offered.Error);
            Assert.Contains(offered.Value!, answer => answer.Transition.Key == SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep && answer.CanRun && answer.Transition.Label == "Mark as handled");
        }

        var adaBefore = await scenario.NoticesOfAsync(this.database, ada, Ct);
        var bus = new NoticeBus(NullLogger<NoticeBus>.Instance);
        var told = new ConcurrentQueue<Guid>();
        var subscriptions = coordinators.Append(ada)
            .Select(user => bus.Subscribe(scenario.TenantId, user, notice =>
            {
                told.Enqueue(notice.MarkedRead ? user : Guid.Empty);
                return Task.CompletedTask;
            }))
            .ToList();
        await using (var handling = scenario.HostOf(this.database, casey, bus))
        {
            var service = handling.Get<SlotAssignmentService>();
            foreach (var (id, key) in new[] { (assigned, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep), (accepted, SlotAssignmentWorkflowAttribute.AcceptedMessageHandledStep) })
            {
                var handled = await AssignmentScenario.StepAsync(service, id, key, Ct);
                Assert.True(handled.IsSuccess, handled.Error);
            }
        }

        subscriptions.ForEach(subscription => subscription.Dispose());
        Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.Accepted], await AssignmentScenario.StatesAsync(this.database, Ct, assigned, accepted));
        var stamps = (await AssignmentScenario.StoredAsync(this.database, assigned, Ct))!;
        Assert.True(stamps.LastCoordinatorCommentAt > stamps.LastStaffCommentAt);
        Assert.True(stamps.StaffSeenAt >= stamps.LastCoordinatorCommentAt);
        Assert.Equal(casey, (await AbsenceScenario.LastStepByAsync(this.database, assigned, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep, Ct)).ActorId);
        Assert.Equal(["Is there a parking card?"], await AssignmentScenario.StaffThreadAsync(this.database, assigned, Ct));
        Assert.Equal(adaBefore, await scenario.NoticesOfAsync(this.database, ada, Ct));
        foreach (var coordinator in coordinators)
        {
            Assert.Equal((0, 0), (await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, assigned, Ct), await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, accepted, Ct)));
            var keys = await scenario.Absence.StaffQuestionKeysAsync(this.database, coordinator, Ct);
            Assert.DoesNotContain($"message-{assigned}", keys);
            Assert.DoesNotContain($"message-{accepted}", keys);
        }

        Assert.Equal(new[] { casey, casey, dana, dana }.Order(), told.Order());
        await using (var teaching = scenario.HostOf(this.database, ada))
        {
            var service = teaching.Get<SlotAssignmentService>();
            Assert.False(Assert.Single((await service.QueryOwnAssignmentsAsync(new ListQuery(), Ct)).Value!.Rows).HasUnseenReply);
            Assert.False(Assert.Single((await service.Conversations.GetSlotAssignmentCommentsAsync(assigned, Ct)).Value!).RequiresAttention);
        }

        await using (var coordinating = scenario.HostOf(this.database, dana))
        {
            Assert.All((await coordinating.Get<StaffConversationService>().GetJobConversationsAsync(job, Ct)).Value!, row => Assert.False(row.AwaitsReply));
        }

        Assert.Contains(SlotAssignmentWorkflowAttribute.AwaitsReplyReason, (await scenario.StepAsync(this.database, dana, assigned, SlotAssignmentWorkflowAttribute.AssignedMessageHandledStep, Ct)).Error, StringComparison.Ordinal);

        await using (var writing = scenario.HostOf(this.database, ada))
        {
            Assert.True((await writing.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assigned, CommentAudience.Staff, "One more thing.", Ct)).IsSuccess);
        }

        Assert.Equal(1, await AbsenceScenario.UnreadAboutAsync(this.database, dana, assigned, Ct));
        var replied = new ConcurrentQueue<Guid>();
        using (bus.Subscribe(scenario.TenantId, dana, notice =>
        {
            replied.Enqueue(notice.MarkedRead ? dana : Guid.Empty);
            return Task.CompletedTask;
        }))
        {
            await using var replying = scenario.HostOf(this.database, casey, bus);
            Assert.True((await replying.Get<StaffConversationService>().AddSlotAssignmentCommentAsync(assigned, CommentAudience.Staff, "Sure, go ahead.", Ct)).IsSuccess);
        }

        foreach (var coordinator in coordinators)
        {
            Assert.Equal(0, await AbsenceScenario.UnreadAboutAsync(this.database, coordinator, assigned, Ct));
        }

        Assert.Equal([dana], replied);
        Assert.Equal(1, await AbsenceScenario.UnreadAboutAsync(this.database, ada, assigned, Ct, MessageTypes.AssignmentCoordinatorWrote));
    }

    private async Task WaitForApplicationLockWaiterAsync()
    {
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND resource_database_id = DB_ID()", connection);
            if ((int)(await command.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail("The post never waited for the thread lock.");
    }

    private Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, Guid id)
    {
        var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 20 };
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQueryAsync(Ct).ContinueWith(run => { command.Dispose(); return run; }, TaskScheduler.Default).Unwrap();
    }

    private sealed class CommentCommitProbe : IOutgoingMessageDispatcher
    {
        private readonly RealDatabase database;

        public CommentCommitProbe(RealDatabase database)
        {
            this.database = database;
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<(IReadOnlyList<OutgoingMessage> Messages, Guid CommentId, bool Committed)> batches = new();

        public IReadOnlyList<(IReadOnlyList<OutgoingMessage> Messages, Guid CommentId, bool Committed)> Batches => [.. this.batches];

        public async Task DispatchAsync(IReadOnlyList<OutgoingMessage> messages, Guid historyEntryId, CancellationToken cancellationToken)
        {
            await using var reader = this.database.CreateSystemContext();
            await reader.Database.OpenConnectionAsync(cancellationToken);
            await reader.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 0", cancellationToken);
            bool committed;
            try
            {
                committed = await reader.Comments.AsNoTracking().AnyAsync(comment => comment.Id == historyEntryId, cancellationToken);
            }
            catch (SqlException exception) when (exception.Number == 1222)
            {
                committed = false;
            }

            this.batches.Enqueue(([.. messages], historyEntryId, committed));
        }
    }

    private static async Task<string?> NoteRefusalPastTheJobServiceAsync(ServiceHost teaching, Guid job) =>
        (await teaching.Get<CommentService>().AddCommentAsync<Job>(job, JobService.InternalThread, "Past the job service", TestContext.Current.CancellationToken)).Error;

    private static Guid Made(Staffora.Core.Models.Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
