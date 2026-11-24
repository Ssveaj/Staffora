using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Jobs;

public sealed class SlotAssignmentService
{
    public const string NoView = "You do not have permission to view assignments.";

    public const string NoStaffing = "You do not have permission to request or assign staff members.";

    public const string NoChange = "You do not have permission to change assignments.";

    public const string NotFound = "Assignment not found.";

    public const string StaffNotFound = "Staff member not found.";

    public const string CreateByRequestOrAssign = "A staff member is asked for or assigned to a slot by requesting or assigning them.";

    public const string ReadFailed = "The assignments could not be read. Try again.";

    public const string NotStaffable = "Staff are asked for or assigned only to an active slot of a job being staffed (coordinating, confirmed or in progress).";

    public const string SlotHeld = "A staff member is assigned to this slot. Cancel the assigned staff member first to staff it anew.";

    public const string TakenBeforeYourYes = "Every slot was taken before your yes arrived, so you were not selected.";

    public const string SystemStepActor = "Automatic";

    public const string GroupFull = "Every slot of the group is held. Cancel an assigned staff member first to staff it anew.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly CustomFieldService attributes;
    private readonly TenantClock tenantClock;
    private readonly StaffConversationService conversations;
    private readonly TenantSettingsService settings;
    private readonly TimeProvider clock;
    private readonly ILogger<SlotAssignmentService> log;

    public SlotAssignmentService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        CustomFieldService attributes,
        TenantClock tenantClock,
        StaffConversationService conversations,
        TenantSettingsService settings,
        TimeProvider clock,
        ILogger<SlotAssignmentService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.attributes = attributes;
        this.tenantClock = tenantClock;
        this.conversations = conversations;
        this.settings = settings;
        this.clock = clock;
        this.log = log;
    }

    public StaffConversationService Conversations => this.conversations;

    private static DetachedEntityUpdaterOptions<SlotAssignment> UpdaterOptions => new DetachedEntityUpdaterOptions<SlotAssignment>()
        .Excluding(assignment => assignment.LastStaffCommentAt)
        .Excluding(assignment => assignment.LastCoordinatorCommentAt)
        .Excluding(assignment => assignment.StaffSeenAt);

    public async Task<Result<ListPage<SlotAssignmentRow>>> QuerySlotAssignmentsAsync(Guid slotId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(JobService.Readers, cancellationToken))
            return Result<ListPage<SlotAssignmentRow>>.Failure(NoView);

        var list = SlotAssignmentList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var groupIds = (await SlotGroupReader.GroupOfAsync(context, slotId, cancellationToken))?.Group.SlotIds ?? [slotId];
            var rows = context.SlotAssignments.AsNoTracking().Where(assignment => groupIds.Contains(assignment.JobSlotId));
            if (list.Search is { Length: > 0 } search)
            {
                rows = rows.Where(assignment => (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Contains(search));
            }

            if (list.Filter(SlotAssignmentList.Status) is { } status && ListOption.TryParseEnum<SlotAssignmentState>(status, out var state))
            {
                rows = rows.Where(assignment => assignment.State == state);
            }

            var total = await rows.CountAsync(cancellationToken);
            var page = ListPage<SlotAssignmentRow>.PageWithin(list, total);
            var order = new ListOrder<SlotAssignment>(rows);
            _ = list.Sort == SlotAssignmentList.SortCreated
                ? order.By(assignment => assignment.Created, list.Descending)
                : order.By(assignment => assignment.Resource.User.FirstName, list.Descending).By(assignment => assignment.Resource.User.LastName, list.Descending).By(assignment => assignment.Created, true);
            var found = await order.By(assignment => assignment.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(assignment => new SlotAssignmentRow(
                    assignment.Id,
                    assignment.JobSlotId,
                    (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(),
                    assignment.State,
                    assignment.Created))
                .ToListAsync(cancellationToken);
            return Result<ListPage<SlotAssignmentRow>>.Success(ListPage<SlotAssignmentRow>.Ungrouped(found, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the staff members of slot {SlotId} failed.", slotId);
            return Result<ListPage<SlotAssignmentRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<ListPage<OwnAssignmentRow>>> QueryOwnAssignmentsAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken))
            return Result<ListPage<OwnAssignmentRow>>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<ListPage<OwnAssignmentRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = OwnAssignmentList.Definition.Normalize(query);
        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<ListPage<OwnAssignmentRow>>.Failure(zone.Error ?? ReadFailed);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var rows = context.SlotAssignments.AsNoTracking().Where(assignment => assignment.TenantId == tenantId && assignment.Resource.UserId == self && assignment.State != SlotAssignmentState.Expired);
            if (list.Search is { Length: > 0 } search)
            {
                rows = rows.Where(assignment => assignment.Slot.Job.Name.Contains(search) || assignment.Slot.Job.Customer.Name.Contains(search));
            }

            var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
            if (list.Filter(OwnAssignmentList.Status) is { } status && ListOption.TryParseEnum<SlotAssignmentState>(status, out var state))
            {
                rows = rows.Where(assignment => assignment.State == state);
            }
            else if (!list.IsOn(OwnAssignmentList.ShowClosed))
            {
                hidden[OwnAssignmentList.ShowClosed] = await rows.CountAsync(assignment => !(assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned), cancellationToken);
                rows = rows.Where(assignment => assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned);
            }

            var total = await rows.CountAsync(cancellationToken);
            var page = ListPage<OwnAssignmentRow>.PageWithin(list, total);
            var order = new ListOrder<SlotAssignment>(rows);
            _ = list.Sort switch
            {
                OwnAssignmentList.SortJob => order.By(assignment => assignment.Slot.Job.Name, list.Descending).By(assignment => assignment.Slot.Name, list.Descending),
                OwnAssignmentList.SortAsked => order.By(assignment => assignment.Created, list.Descending),
                _ => order.By(assignment => assignment.Slot.Job.PeriodStart, list.Descending).By(assignment => assignment.Slot.Job.Name, list.Descending),
            };
            var found = await order.By(assignment => assignment.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(ownRow)
                .ToListAsync(cancellationToken);
            return Result<ListPage<OwnAssignmentRow>>.Success(ListPage<OwnAssignmentRow>.Ungrouped([.. found.Select(row => WithAnswerBy(row, timeZone))], total, page, list.PageSize) with { HiddenCounts = hidden });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the own assignments of user {UserId} failed.", self);
            return Result<ListPage<OwnAssignmentRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<OwnWork>> ReadOwnWorkAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken))
            return Result<OwnWork>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<OwnWork>.Failure(IRuntimeExecutionContext.NoTenant);

        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<OwnWork>.Failure(zone.Error ?? ReadFailed);

        var today = this.tenantClock.TodayIn(timeZone);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var own = context.SlotAssignments.AsNoTracking().Where(assignment => assignment.TenantId == tenantId && assignment.Resource.UserId == self);
            var requests = own.Where(assignment => assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion);
            var next = own.Where(assignment => assignment.State == SlotAssignmentState.Assigned && assignment.Slot.Job.PeriodEnd >= today);

            var unread = own.Where(assignment => (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned)
                && assignment.LastCoordinatorCommentAt != null
                && (assignment.StaffSeenAt == null || assignment.LastCoordinatorCommentAt > assignment.StaffSeenAt));

            var requestCount = await requests.CountAsync(cancellationToken);
            var nextCount = await next.CountAsync(cancellationToken);
            var unreadCount = await unread.CountAsync(cancellationToken);
            var shownRequests = await Earliest(requests).Take(OwnWork.Shown).Select(ownRow).ToListAsync(cancellationToken);
            var shownNext = await Earliest(next).Take(OwnWork.Shown).Select(ownRow).ToListAsync(cancellationToken);
            var shownUnread = unreadCount == 0
                ? []
                : await unread.OrderByDescending(assignment => assignment.LastCoordinatorCommentAt).ThenBy(assignment => assignment.Id).Take(OwnWork.Shown).Select(ownRow).ToListAsync(cancellationToken);
            return Result<OwnWork>.Success(new OwnWork([.. shownRequests.Select(row => WithAnswerBy(row, timeZone))], requestCount, shownNext, nextCount) { Unread = shownUnread, UnreadCount = unreadCount });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the own work of user {UserId} failed.", self);
            return Result<OwnWork>.Failure(ReadFailed);
        }

        static IQueryable<SlotAssignment> Earliest(IQueryable<SlotAssignment> rows) => rows
            .OrderBy(assignment => assignment.Slot.Job.PeriodStart)
            .ThenBy(assignment => assignment.Slot.Job.Name)
            .ThenBy(assignment => assignment.Slot.Name)
            .ThenBy(assignment => assignment.Id);
    }

    private static readonly System.Linq.Expressions.Expression<Func<SlotAssignment, OwnAssignmentRow>> ownRow = assignment => new OwnAssignmentRow(
        assignment.Id,
        assignment.Slot.Job.Name,
        assignment.Slot.Name,
        assignment.Slot.Job.Customer.Name,
        assignment.Slot.Job.PeriodStart,
        assignment.Slot.Job.PeriodEnd,
        assignment.State,
        assignment.Created,
        assignment.LastCoordinatorCommentAt != null && (assignment.StaffSeenAt == null || assignment.LastCoordinatorCommentAt > assignment.StaffSeenAt))
    {
        Place = JobPlace.ShortTextOf(assignment.Slot.Job.PlaceName, assignment.Slot.Job.City),
        Times = new JobTimes(assignment.Slot.Job.Schedule, assignment.Slot.Job.PeriodStart, assignment.Slot.Job.PeriodEnd, assignment.Slot.Job.StartTime, assignment.Slot.Job.EndTime, new List<OccurrenceTimes>()),
        RespondBy = assignment.RespondBy,
    };

    private static OwnAssignmentRow WithAnswerBy(OwnAssignmentRow row, TimeZoneInfo zone) =>
        AnswerByText(row.State, row.RespondBy, zone) is { } answerBy ? row with { AnswerBy = answerBy } : row;

    internal static string? AnswerByText(SlotAssignmentState state, DateTime? respondBy, TimeZoneInfo zone) =>
        SlotAssignmentWorkflowAttribute.IsAsked(state) && respondBy is { } by ? ResponseWindow.AnswerByText(TenantClock.ToLocal(by, zone)) : null;

    internal Task<Result<ResponseWindow>> DefaultResponseWindowAsync(CancellationToken cancellationToken) =>
        this.settings.GetAsync(TenantSettingDefinitions.RespondWithin, cancellationToken);

    internal static async Task<Result<DateTime?>> RespondByAsync(TenantSettingsService settings, TimeProvider clock, ResponseWindow? within, CancellationToken cancellationToken)
    {
        if (within is null)
        {
            var configured = await settings.GetAsync(TenantSettingDefinitions.RespondWithin, cancellationToken);
            if (!configured.IsSuccess || configured.Value is null)
                return Result<DateTime?>.Failure(configured.Error);

            within = configured.Value;
        }

        return Result<DateTime?>.Success(within.DeadlineAfter(clock.GetUtcNow().UtcDateTime));
    }

    public Task<Result<Guid>> RequestStaffAsync(Guid slotId, Guid resourceId, CancellationToken cancellationToken = default) =>
        StaffSlotAsync(slotId, resourceId, SlotAssignmentWorkflowAttribute.RequestStep, null, cancellationToken);

    public Task<Result<Guid>> RequestStaffAsync(Guid slotId, Guid resourceId, ResponseWindow within, CancellationToken cancellationToken = default) =>
        StaffSlotAsync(slotId, resourceId, SlotAssignmentWorkflowAttribute.RequestStep, within, cancellationToken);

    public Task<Result<Guid>> AssignStaffAsync(Guid slotId, Guid resourceId, CancellationToken cancellationToken = default) =>
        StaffSlotAsync(slotId, resourceId, SlotAssignmentWorkflowAttribute.AssignStep, null, cancellationToken);

    private async Task<Result<Guid>> StaffSlotAsync(Guid slotId, Guid resourceId, string transitionKey, ResponseWindow? within, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.PerformCoordination, cancellationToken))
            return Result<Guid>.Failure(NoStaffing);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Guid>.Failure(JobService.ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        if (transitionKey is not (SlotAssignmentWorkflowAttribute.RequestStep or SlotAssignmentWorkflowAttribute.AssignStep))
            return Result<Guid>.Failure(CreateByRequestOrAssign);

        var row = new SlotAssignment
        {
            TenantId = tenantId,
            State = SlotAssignmentState.Create,
            JobSlotId = slotId,
            ResourceId = resourceId,
        };

        try
        {
            if (transitionKey == SlotAssignmentWorkflowAttribute.RequestStep)
            {
                var respondBy = await RespondByAsync(this.settings, this.clock, within, cancellationToken);
                if (!respondBy.IsSuccess)
                    return Result<Guid>.Failure(respondBy.Error);

                row.RespondBy = respondBy.Value;
            }

            return await this.workflows.CreateAndTransitionAsync<SlotAssignmentState, SlotAssignment>(row, transitionKey, SlotAssignmentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Staffing slot {SlotId} with staff member {ResourceId} failed.", slotId, resourceId);
            return Result<Guid>.Failure("An error occurred while staffing the slot.");
        }
    }

    public async Task<Result<DetachedEntityUpdater<SlotAssignment>>> LoadSlotAssignmentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        var (readsAll, readsOwn) = await ReaderAsync(cancellationToken);
        if (!readsAll && !readsOwn)
            return Result<DetachedEntityUpdater<SlotAssignment>>.Failure(NoView);

        var self = this.runtime.EffectiveUserId;
        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var assignment = await context.SlotAssignments
                .WithWorkflowGraph()
                .AsNoTrackingWithIdentityResolution()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            if (assignment is null || (!readsAll && assignment.Resource.UserId != self))
                return Result<DetachedEntityUpdater<SlotAssignment>>.Failure(NotFound);

            if (!readsAll)
            {
                foreach (var slot in assignment.Slot.Job.Slots.Append(assignment.Slot))
                {
                    slot.Assignments = [.. slot.Assignments.Where(other => other.Id == assignment.Id)];
                }

                if (assignment.Round is { } round)
                {
                    round.Requests = [.. round.Requests.Where(other => other.Id == assignment.Id)];
                }

                if (assignment.State != SlotAssignmentState.Assigned)
                {
                    assignment.Slot.Job.AccessNotes = null;
                }
            }

            return Result<DetachedEntityUpdater<SlotAssignment>>.Success(new DetachedEntityUpdater<SlotAssignment>(assignment, context, UpdaterOptions));
        }, this.log, $"the assignment {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<SlotAssignmentEditorOptions>> GetSlotAssignmentEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (id is not { } assignmentId)
            return Result<SlotAssignmentEditorOptions>.Success(new SlotAssignmentEditorOptions([]));

        var (refusal, readsAll) = await MayReadAsync(assignmentId, cancellationToken);
        if (refusal is not null)
            return Result<SlotAssignmentEditorOptions>.Failure(refusal);

        SlotAssignmentAbout about;
        try
        {
            var self = this.runtime.EffectiveUserId;
            var ownReader = await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken);
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var read = await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.Id == assignmentId)
                .Select(assignment => new
                {
                    assignment.State,
                    assignment.RespondBy,
                    Owner = assignment.Resource.UserId,
                    Service = assignment.Slot.Service != null ? assignment.Slot.Service.Name : null,
                    Qualifications = assignment.Slot.Qualifications.Select(required => new { required.Qualification.Name, required.Level }).ToList(),
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (read is null)
                return Result<SlotAssignmentEditorOptions>.Failure(NotFound);

            string? answerBy = null;
            if (SlotAssignmentWorkflowAttribute.IsAsked(read.State) && read.RespondBy is not null)
            {
                var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
                if (!zone.IsSuccess || zone.Value is not { } timeZone)
                    return Result<SlotAssignmentEditorOptions>.Failure(zone.Error ?? ReadFailed);

                answerBy = AnswerByText(read.State, read.RespondBy, timeZone);
            }

            var own = ownReader && read.Owner == self;
            about = new SlotAssignmentAbout(
                own,
                read.Service,
                [.. read.Qualifications
                    .OrderBy(required => required.Level)
                    .ThenBy(required => required.Name, StringComparer.CurrentCulture)
                    .Select(required => required.Level == RequirementLevel.Required ? required.Name : $"{required.Name} (preferred)")],
                own ? OwnAssignmentRow.NextLabelOf(read.State) : null,
                own ? OwnAssignmentRow.NextHintOf(read.State) : null,
                answerBy);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading what the slot of the assignment {AssignmentId} asks for failed.", assignmentId);
            return Result<SlotAssignmentEditorOptions>.Failure(ReadFailed);
        }

        if (!readsAll)
        {
            var theirs = await this.attributes.ReadAssignedStaffJobFieldsAsync(assignmentId, cancellationToken);
            return theirs.IsSuccess && theirs.Value is { } read
                ? Result<SlotAssignmentEditorOptions>.Success(about.With([.. read.Select(field => new AssignmentJobField(
                    field.Field.Id,
                    field.Field.Name,
                    AttributeValues.DisplayText(field.Value, field.Field),
                    field.Field.TenantId is null))]))
                : Result<SlotAssignmentEditorOptions>.Failure(theirs.Error);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var read = await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.Id == assignmentId)
                .Select(assignment => new { assignment.Slot.JobId, assignment.TenantId })
                .FirstOrDefaultAsync(cancellationToken);
            if (read is null)
                return Result<SlotAssignmentEditorOptions>.Failure(NotFound);

            var all = await this.attributes.GetDefinitionsAsync<JobAttributeDefinition>(read.TenantId);
            if (!all.IsSuccess || all.Value is null)
                return Result<SlotAssignmentEditorOptions>.Failure(all.Error);

            var fields = all.Value;
            var ids = fields.Select(field => field.Id).ToList();
            List<JobAttribute> values = ids.Count == 0
                ? []
                : await context.JobAttributes.AsNoTracking()
                    .Where(value => value.EntityId == read.JobId && ids.Contains(value.EntityAttributeDefinitionId))
                    .ToListAsync(cancellationToken);
            return Result<SlotAssignmentEditorOptions>.Success(about.With([.. fields.Select(field => new AssignmentJobField(
                field.Id,
                field.Name,
                AttributeValues.DisplayText(values.FirstOrDefault(value => value.EntityAttributeDefinitionId == field.Id), field),
                field.TenantId is null,
                JobFieldAudiences.Label(field.Audience)))]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the job fields of the assignment {AssignmentId} failed.", assignmentId);
            return Result<SlotAssignmentEditorOptions>.Failure(ReadFailed);
        }
    }

    public async Task<Result> UpdateSlotAssignmentAndTransitionAsync(DetachedEntityUpdater<SlotAssignment> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasAnyPermissionAsync([RolePermission.PerformCoordination, RolePermission.RespondToAssignment], cancellationToken))
            return Result.Failure(NoChange);

        if (this.runtime.Current.HasPlatformReach())
            return Result.Failure(JobService.ChangeInOwnTenantOnly);

        var id = (Guid)updater.OriginalValue(nameof(SlotAssignment.Id))!;
        if (updater.HasChanges())
            return Result.Failure(TransitionRefusals.PendingEdits);

        try
        {
            var taken = await this.workflows.SaveAndTransitionAsync<SlotAssignmentState, SlotAssignment>(updater, transitionKey, SlotAssignmentWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);

            if (!taken.IsSuccess && SlotAssignmentWorkflowAttribute.IsAcceptStep(transitionKey))
            {
                await using var context = this.dbFactory.CreateDbContext(this.runtime);
                if (await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.Id == id).Select(assignment => (SlotAssignmentState?)assignment.State).FirstOrDefaultAsync(cancellationToken) == SlotAssignmentState.NotSelected)
                {
                    return Result.Failure(TakenBeforeYourYes);
                }
            }

            return taken;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Taking a step on the assignment {AssignmentId} failed.", id);
            return Result.Failure("An error occurred while saving the assignment.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetSlotAssignmentHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken)
    {
        var (refusal, readsAll) = await MayReadAsync(id, cancellationToken);
        if (refusal is not null)
            return Result<StateHistoryPage>.Failure(refusal);

        var page = await this.history.GetHistoryAsync<SlotAssignment>(id, query, cancellationToken);
        if (readsAll || !page.IsSuccess || page.Value is not { } steps)
            return page;

        var workflow = WorkflowRegistry.Default.Get<SlotAssignmentState, SlotAssignment>();
        return Result<StateHistoryPage>.Success(steps with
        {
            Steps = [.. steps.Steps.Select(step => step with
            {
                Notices = null,
                Actor = workflow.TryGetTransition(step.TransitionKey, out var transition) && transition.IsSystemDriven ? SystemStepActor : step.Actor,
            })],
        });
    }

    private async Task<(bool ReadsAll, bool ReadsOwn)> ReaderAsync(CancellationToken cancellationToken) =>
        (await this.permissionService.HasAnyPermissionAsync(JobService.Readers, cancellationToken), await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken));

    private async Task<(string? Refusal, bool ReadsAll)> MayReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var (readsAll, readsOwn) = await ReaderAsync(cancellationToken);
        if (!readsAll && !readsOwn)
            return (NoView, false);

        var self = this.runtime.EffectiveUserId;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var owner = await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.Id == id).Select(assignment => (Guid?)assignment.Resource.UserId).FirstOrDefaultAsync(cancellationToken);
        return owner is null || (!readsAll && owner != self) ? (NotFound, readsAll) : (null, readsAll);
    }
}

public sealed record SlotAssignmentEditorOptions(IReadOnlyList<AssignmentJobField> JobFields)
{
    public bool ForStaff { get; init; }

    public string? Service { get; init; }

    public IReadOnlyList<string> Qualifications { get; init; } = [];

    public string? Status { get; init; }

    public string? StatusHint { get; init; }

    public string? AnswerBy { get; init; }
}

internal sealed record SlotAssignmentAbout(bool ForStaff, string? Service, IReadOnlyList<string> Qualifications, string? Status, string? StatusHint, string? AnswerBy)
{
    public SlotAssignmentEditorOptions With(IReadOnlyList<AssignmentJobField> jobFields) =>
        new(jobFields) { ForStaff = ForStaff, Service = Service, Qualifications = Qualifications, Status = Status, StatusHint = StatusHint, AnswerBy = AnswerBy };
}

