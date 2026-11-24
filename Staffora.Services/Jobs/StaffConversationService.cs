using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Comments;
using Staffora.Services.Coordination;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed class StaffConversationService
{
    public const string ThreadClosedForStaff = "This conversation is closed: your request or assignment is no longer open.";

    public const string WrittenThroughSteps = "While the request is open, questions and answers are written in the window that opens from a step below.";

    public const string ThreadClosedWithJob = "This conversation is closed: the job is completed or canceled.";

    private static readonly CommentThreadAccess coordinatorThread = new(CommentAudience.Staff, RolePermission.ViewCoordination, RolePermission.PerformCoordination);

    private static readonly CommentThreadAccess ownThread = new(CommentAudience.Staff, RolePermission.ViewOwnAssignments, RolePermission.RespondToAssignment);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly CommentService comments;
    private readonly IHandledNotices handledNotices;
    private readonly TenantClock tenantClock;
    private readonly TimeProvider clock;
    private readonly ILogger<StaffConversationService> log;

    public StaffConversationService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        CommentService comments,
        IHandledNotices handledNotices,
        TenantClock tenantClock,
        TimeProvider clock,
        ILogger<StaffConversationService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.comments = comments;
        this.handledNotices = handledNotices;
        this.tenantClock = tenantClock;
        this.clock = clock;
        this.log = log;
    }

    public async Task<Result<IReadOnlyList<CommentThread>>> GetSlotAssignmentCommentsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await MayReadAsync(id, cancellationToken) is { } refusal)
            return Result<IReadOnlyList<CommentThread>>.Failure(refusal);

        try
        {
            var (access, facts) = await ThreadAccessAsync(id, cancellationToken);
            if (facts is null)
                return Result<IReadOnlyList<CommentThread>>.Failure(SlotAssignmentService.NotFound);

            var own = access.ReadPermission == RolePermission.ViewOwnAssignments;
            CommentTurn turn;
            if (own)
            {
                var unseen = StaffConversation.HasUnseenReply(facts.LastCoordinatorCommentAt, facts.StaffSeenAt);
                if (unseen && !this.runtime.IsImpersonating)
                {
                    await MarkSeenAsync(id, cancellationToken);
                }

                turn = new CommentTurn(unseen, CommentTurn.NewReply, "New", facts.StaffSeenAt);
            }
            else
            {
                var awaits = StaffConversation.AwaitsCoordinatorReply(facts.LastStaffCommentAt, facts.LastCoordinatorCommentAt);
                turn = new CommentTurn(awaits, CommentTurn.AwaitsYourReply, "Unanswered", awaits ? await LastCoordinatorCommentStoredAsync(id, facts.Owner, cancellationToken) : null);
            }

            var threads = await this.comments.GetThreadsAsync<SlotAssignment>(id, [access], cancellationToken);
            return threads.IsSuccess && threads.Value is { } read
                ? Result<IReadOnlyList<CommentThread>>.Success([.. read.Select(thread => thread with { Turn = turn })])
                : threads;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the conversation of the assignment {AssignmentId} failed.", id);
            return Result<IReadOnlyList<CommentThread>>.Failure(SlotAssignmentService.ReadFailed);
        }
    }

    public async Task<Result<CommentThread>> AddSlotAssignmentCommentAsync(Guid id, CommentAudience audience, string text, CancellationToken cancellationToken)
    {
        if (audience != CommentAudience.Staff)
            return Result<CommentThread>.Failure(CommentService.NoAccess);

        if (await MayReadAsync(id, cancellationToken) is { } refusal)
            return Result<CommentThread>.Failure(refusal);

        CommentThreadAccess access;
        ThreadFacts? facts;
        try
        {
            (access, facts) = await ThreadAccessAsync(id, cancellationToken);
            if (facts is null)
                return Result<CommentThread>.Failure(SlotAssignmentService.NotFound);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the conversation of the assignment {AssignmentId} failed.", id);
            return Result<CommentThread>.Failure(SlotAssignmentService.ReadFailed);
        }

        var staffSide = access.ReadPermission == RolePermission.ViewOwnAssignments;
        string? author = null;
        if (!staffSide)
        {
            try
            {
                author = await PosterNameAsync(facts.TenantId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                this.log.LogError(exception, "Reading the conversation of the assignment {AssignmentId} failed.", id);
                return Result<CommentThread>.Failure(SlotAssignmentService.ReadFailed);
            }
        }

        var settled = new List<Guid>();
        var posted = await this.comments.AddCommentAsync<SlotAssignment>(id, access with { Effects = PostEffects(id, staffSide, facts, author, settled) }, text, cancellationToken);
        if (posted.IsSuccess && settled.Count > 0)
        {
            this.handledNotices.TellReaders(facts.TenantId, settled);
        }

        return posted.IsSuccess && posted.Value is { } thread
            ? Result<CommentThread>.Success(thread with { Turn = CommentTurn.Settled }, posted.Message)
            : posted;
    }

    public async Task<Result<IReadOnlyList<StaffConversationRow>>> GetJobConversationsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<IReadOnlyList<StaffConversationRow>>.Failure(CommentService.NoAccess);

        if (this.runtime.Current.TenantId is null)
            return Result<IReadOnlyList<StaffConversationRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var assignments = await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.Slot.JobId == jobId)
                .Select(assignment => new
                {
                    assignment.Id,
                    assignment.TenantId,
                    Staff = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(),
                    Slot = assignment.Slot.Name,
                    assignment.State,
                    assignment.LastStaffCommentAt,
                    assignment.LastCoordinatorCommentAt,
                })
                .ToListAsync(cancellationToken);
            if (assignments.Count == 0)
                return Result<IReadOnlyList<StaffConversationRow>>.Success([]);

            var ids = assignments.Select(assignment => assignment.Id).ToList();
            var tenantId = assignments[0].TenantId;
            var entityType = CommentThreadWriter.EntityTypeOf(context, typeof(SlotAssignment));
            var threadComments = context.Comments.AsNoTracking()
                .Where(comment => comment.TenantId == tenantId
                    && comment.CommentContainer!.EntityType == entityType
                    && comment.CommentContainer.Audience == CommentAudience.Staff
                    && ids.Contains(comment.CommentContainer.EntityId));
            var counts = await threadComments
                .GroupBy(comment => comment.CommentContainer!.EntityId)
                .Select(group => new { AssignmentId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(group => group.AssignmentId, group => group.Count, cancellationToken);

            var newest = (await threadComments
                .Where(comment => comment.Created == context.Comments.Where(other => other.CommentContainerId == comment.CommentContainerId).Max(other => other.Created))
                .Select(comment => new { AssignmentId = comment.CommentContainer!.EntityId, comment.Id, comment.AuthorType, comment.CreatedById, comment.Created, comment.Text })
                .ToListAsync(cancellationToken))
                .GroupBy(comment => comment.AssignmentId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(comment => comment.Id).First());

            var authorIds = newest.Values.Select(comment => comment.CreatedById).Distinct().ToList();
            var authors = authorIds.Count == 0
                ? []
                : await context.Users.AsNoTracking()
                    .Where(user => authorIds.Contains(user.Id) && user.TenantId == tenantId)
                    .Select(user => new { user.Id, user.FirstName, user.LastName, user.IsSystemAccount })
                    .ToDictionaryAsync(user => user.Id, user => user.IsSystemAccount ? StateHistoryService.SystemAccountName : $"{user.FirstName} {user.LastName}".Trim(), cancellationToken);

            var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
            if (!zone.IsSuccess || zone.Value is null)
                return Result<IReadOnlyList<StaffConversationRow>>.Failure(zone.Error ?? SlotAssignmentService.ReadFailed);

            var rows = assignments
                .Where(assignment => SlotAssignmentWorkflowAttribute.IsLive(assignment.State) || counts.ContainsKey(assignment.Id))
                .Select(assignment =>
                {
                    var last = newest.GetValueOrDefault(assignment.Id);
                    return new StaffConversationRow(
                        assignment.Id,
                        assignment.Staff,
                        assignment.Slot,
                        assignment.State,
                        StaffConversation.AwaitsCoordinatorReply(assignment.LastStaffCommentAt, assignment.LastCoordinatorCommentAt),
                        counts.GetValueOrDefault(assignment.Id),
                        last is null ? null : TenantClock.ToLocal(last.Created, zone.Value),
                        last is null ? null : last.AuthorType == CommentAuthorType.System ? CommentService.SystemAuthor : authors.GetValueOrDefault(last.CreatedById, CommentService.OutsideAuthor),
                        last is null ? null : StaffConversationRow.Excerpt(last.Text));
                })
                .OrderByDescending(row => row.AwaitsReply)
                .ThenByDescending(row => row.LastAt)
                .ThenBy(row => row.Staff, StringComparer.CurrentCulture)
                .ThenBy(row => row.AssignmentId)
                .ToList();
            return Result<IReadOnlyList<StaffConversationRow>>.Success(rows);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the staff members' conversations of job {JobId} failed.", jobId);
            return Result<IReadOnlyList<StaffConversationRow>>.Failure(SlotAssignmentService.ReadFailed);
        }
    }

    private async Task<string?> MayReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var readsAll = await this.permissionService.HasAnyPermissionAsync(JobService.Readers, cancellationToken);
        var readsOwn = await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken);
        if (!readsAll && !readsOwn)
            return SlotAssignmentService.NoView;

        var self = this.runtime.EffectiveUserId;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var owner = await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.Id == id).Select(assignment => (Guid?)assignment.Resource.UserId).FirstOrDefaultAsync(cancellationToken);
        return owner is null || (!readsAll && owner != self) ? SlotAssignmentService.NotFound : null;
    }

    private async Task MarkSeenAsync(Guid id, CancellationToken cancellationToken)
    {
        var self = this.runtime.EffectiveUserId;
        var now = this.clock.GetUtcNow().UtcDateTime;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        await context.SlotAssignments
            .Where(assignment => assignment.Id == id && assignment.Resource.UserId == self)
            .ExecuteUpdateAsync(set => set.SetProperty(
                assignment => assignment.StaffSeenAt,
                assignment => assignment.LastCoordinatorCommentAt != null && assignment.LastCoordinatorCommentAt > now ? assignment.LastCoordinatorCommentAt : now), cancellationToken);
    }

    private CommentPostEffects PostEffects(Guid id, bool staffSide, ThreadFacts facts, string? author, List<Guid> settled)
    {
        var self = this.runtime.EffectiveUserId;
        var marksSeen = staffSide && !this.runtime.IsImpersonating;
        return new CommentPostEffects(
            async (context, now, token) =>
            {
                var row = context.SlotAssignments.Where(assignment => assignment.Id == id);
                var stamped = !staffSide
                    ? await row.ExecuteUpdateAsync(set => set.SetProperty(
                        assignment => assignment.LastCoordinatorCommentAt,
                        assignment => assignment.StaffSeenAt != null
                            && assignment.StaffSeenAt >= (assignment.LastStaffCommentAt != null && assignment.LastStaffCommentAt >= now ? assignment.LastStaffCommentAt.Value.AddMilliseconds(1) : now)
                                ? assignment.StaffSeenAt.Value.AddMilliseconds(1)
                                : assignment.LastStaffCommentAt != null && assignment.LastStaffCommentAt >= now ? assignment.LastStaffCommentAt.Value.AddMilliseconds(1) : now), token)
                    : marksSeen
                        ? await row.ExecuteUpdateAsync(set => set
                            .SetProperty(
                                assignment => assignment.LastStaffCommentAt,
                                assignment => assignment.LastCoordinatorCommentAt != null && assignment.LastCoordinatorCommentAt >= now ? assignment.LastCoordinatorCommentAt.Value.AddMilliseconds(1) : now)
                            .SetProperty(
                                assignment => assignment.StaffSeenAt,
                                assignment => assignment.LastCoordinatorCommentAt != null && assignment.LastCoordinatorCommentAt > now ? assignment.LastCoordinatorCommentAt : now), token)
                        : await row.ExecuteUpdateAsync(set => set.SetProperty(
                            assignment => assignment.LastStaffCommentAt,
                            assignment => assignment.LastCoordinatorCommentAt != null && assignment.LastCoordinatorCommentAt >= now ? assignment.LastCoordinatorCommentAt.Value.AddMilliseconds(1) : now), token);
                if (!staffSide && stamped > 0)
                {
                    settled.Clear();
                    settled.AddRange(await this.handledNotices.MarkReadAsync(context, facts.TenantId, typeof(SlotAssignment), id, SlotAssignmentWorkflowAttribute.StaffNoticeTypes, token));
                }

                return stamped > 0;
            },
            (context, _, _) =>
            {
                var change = new TenantChange(facts.TenantId, new HashSet<Guid> { facts.JobId }, new HashSet<Guid> { facts.ResourceId });
                var entityType = CommentThreadWriter.EntityTypeOf(context, typeof(SlotAssignment));
                OutgoingMessage Notice(MessageType type, Dictionary<string, string?> values, List<Guid> recipients, RolePermission hold)
                {
                    var rendered = MessageCatalogue.Render(type, values);
                    return new OutgoingMessage
                    {
                        TenantId = facts.TenantId,
                        MessageType = type.Key,
                        Category = type.Category,
                        EntityType = entityType,
                        EntityId = id,
                        Subject = rendered.Subject,
                        Text = rendered.Text,
                        Link = rendered.Link,
                        RecipientUserIds = recipients,
                        NamedRecipientsHold = hold,
                    };
                }

                if (staffSide)
                {
                    var coordinators = new[] { facts.Coordinator, (Guid?)facts.CreatedById }
                        .OfType<Guid>()
                        .Where(user => user != Guid.Empty && user != self)
                        .Distinct()
                        .ToList();
                    return Task.FromResult(coordinators.Count == 0
                        ? new CommentPostOutcome([], change)
                        : new CommentPostOutcome([Notice(
                            MessageTypes.AssignmentStaffWrote,
                            new() { ["Staff"] = facts.Staff, ["Job"] = facts.Job, ["Slot"] = facts.Slot, ["AssignmentId"] = id.ToString() },
                            coordinators,
                            RolePermission.ViewCoordination)], change));
                }

                if (facts.Owner == self)
                    return Task.FromResult(new CommentPostOutcome([], change));

                return Task.FromResult(new CommentPostOutcome([Notice(
                    MessageTypes.AssignmentCoordinatorWrote,
                    new() { ["FirstName"] = facts.StaffFirstName, ["Author"] = string.IsNullOrWhiteSpace(author) ? "A coordinator" : author, ["Job"] = facts.Job, ["Slot"] = facts.Slot, ["AssignmentId"] = id.ToString() },
                    [facts.Owner],
                    RolePermission.ViewOwnAssignments)], change));
            });
    }

    private async Task<string?> PosterNameAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var self = this.runtime.EffectiveUserId;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Users.AsNoTracking()
            .Where(user => user.Id == self && user.TenantId == tenantId)
            .Select(user => (user.FirstName + " " + user.LastName).Trim())
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<DateTime?> LastCoordinatorCommentStoredAsync(Guid id, Guid staffUserId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var entityType = CommentThreadWriter.EntityTypeOf(context, typeof(SlotAssignment));
        return await context.Comments.AsNoTracking()
            .Where(comment => comment.CommentContainer!.EntityType == entityType
                && comment.CommentContainer.EntityId == id
                && comment.CommentContainer.Audience == CommentAudience.Staff
                && comment.Kind == CommentKind.Comment
                && comment.AuthorType == CommentAuthorType.User
                && comment.CreatedById != staffUserId)
            .MaxAsync(comment => (DateTime?)comment.Created, cancellationToken);
    }

    private async Task<(CommentThreadAccess Access, ThreadFacts? Facts)> ThreadAccessAsync(Guid id, CancellationToken cancellationToken)
    {
        var self = this.runtime.EffectiveUserId;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var read = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.Id == id)
            .Select(assignment => new ThreadFacts(
                assignment.Resource.UserId,
                assignment.State,
                assignment.Slot.Job.State,
                assignment.LastStaffCommentAt,
                assignment.LastCoordinatorCommentAt,
                assignment.StaffSeenAt,
                assignment.TenantId,
                assignment.CreatedById,
                assignment.ResourceId,
                assignment.Slot.JobId,
                assignment.Slot.Job.Name,
                assignment.Slot.Job.CoordinatorUserId,
                assignment.Slot.Name,
                assignment.Resource.User.FirstName,
                (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim()))
            .FirstOrDefaultAsync(cancellationToken);
        var own = read is not null && read.Owner == self && await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken);
        var closed = read is null ? null
            : !JobRules.StaffingOpen(read.JobState) ? ThreadClosedWithJob
            : SlotAssignmentWorkflowAttribute.IsAsked(read.State) ? WrittenThroughSteps
            : own && !SlotAssignmentWorkflowAttribute.IsLive(read.State) ? ThreadClosedForStaff
            : null;
        return ((own ? ownThread : coordinatorThread) with { ClosedReason = closed, ShowsClosedReason = closed != WrittenThroughSteps }, read);
    }

    private sealed record ThreadFacts(
        Guid Owner,
        SlotAssignmentState State,
        JobState JobState,
        DateTime? LastStaffCommentAt,
        DateTime? LastCoordinatorCommentAt,
        DateTime? StaffSeenAt,
        Guid TenantId,
        Guid CreatedById,
        Guid ResourceId,
        Guid JobId,
        string Job,
        Guid? Coordinator,
        string Slot,
        string StaffFirstName,
        string Staff);
}
