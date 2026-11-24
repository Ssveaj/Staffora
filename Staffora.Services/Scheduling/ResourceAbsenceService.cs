using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Comments;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Scheduling;

public sealed class ResourceAbsenceService
{
    public static readonly IReadOnlyList<RolePermission> Readers = [RolePermission.ViewResourceAbsence, RolePermission.ManageResourceAbsence, RolePermission.ApproveResourceAbsence];

    public const string WrittenThroughSteps = "Questions and answers are written in the window that opens from a step below.";

    public static readonly CommentThreadAccess OwnThread = new(CommentAudience.Staff, RolePermission.ReportOwnAbsence, RolePermission.ReportOwnAbsence) { ClosedReason = WrittenThroughSteps, ShowsClosedReason = false };

    public static readonly CommentThreadAccess ApproverThread = new(CommentAudience.Staff, RolePermission.ApproveResourceAbsence, RolePermission.ApproveResourceAbsence) { ClosedReason = WrittenThroughSteps, ShowsClosedReason = false };

    public const string NoView = "You do not have permission to view absences.";

    public const string NoReport = "You may not report or register an absence for this staff member.";

    public const string NoChange = "You do not have permission to change absences.";

    public const string NotFound = "Absence not found.";

    public const string StaffNotFound = "Staff member not found.";

    public const string CreateByReportOrRegister = "An absence is created by reporting your own or registering one on a staff member's behalf.";

    public const string ReadFailed = "The absences could not be read. Try again.";

    public const string NoImpactView = StaffAvailabilityService.NoViewCoordination;

    public const string ImpactNotOnConsole = "The platform console keeps no organization's staffing. Impersonate one of its users to see what an absence affects.";

    public const string ReasonWithApprovers = "The reason for a decision is kept between the staff member and the approvers.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly CommentService comments;
    private readonly TenantClock clock;
    private readonly ILogger<ResourceAbsenceService> log;

    public ResourceAbsenceService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        CommentService comments,
        TenantClock clock,
        ILogger<ResourceAbsenceService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.comments = comments;
        this.clock = clock;
        this.log = log;
    }

    private static DetachedEntityUpdaterOptions<ResourceAbsence> UpdaterOptions => new();

    public async Task<Result<ListPage<AbsenceRow>>> QueryAbsencesAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        var session = this.runtime.Current;
        var readsAll = await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken);
        if (!readsAll && !await this.permissionService.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken))
            return Result<ListPage<AbsenceRow>>.Failure(NoView);

        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } reader)
            return Result<ListPage<AbsenceRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = AbsenceList.Definition.Normalize(query);
        return await ReadPageAsync(tenantId, readsAll ? null : reader, list, AbsenceList.Status, AbsenceList.Kind, list.Sort == AbsenceList.SortStaff, cancellationToken);
    }

    public async Task<Result<ListPage<AbsenceRow>>> QueryAbsencesOfUserAsync(Guid userId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<AbsenceRow>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<AbsenceRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = StaffAbsenceList.Definition.Normalize(query);
        return await ReadPageAsync(tenantId, userId, list, StaffAbsenceList.Status, StaffAbsenceList.Kind, byStaff: false, cancellationToken);
    }

    public async Task<Result<OwnAbsenceStatus>> ReadOwnAbsenceStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken))
            return Result<OwnAbsenceStatus>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<OwnAbsenceStatus>.Failure(IRuntimeExecutionContext.NoTenant);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<OwnAbsenceStatus>.Failure(today.Error ?? ReadFailed);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var ahead = context.ResourceAbsences.AsNoTracking().Where(absence =>
                absence.TenantId == tenantId
                && absence.Resource.UserId == self
                && AbsenceRules.ScheduledStates.Contains(absence.State)
                && absence.LastDay >= today.Value);

            var count = await ahead.CountAsync(cancellationToken);
            var waiting = await ahead.CountAsync(absence => AbsenceRules.UndecidedStates.Contains(absence.State), cancellationToken);
            var rows = await ahead
                .OrderBy(absence => absence.FirstDay)
                .ThenBy(absence => absence.StartTime)
                .ThenBy(absence => absence.Id)
                .Take(OwnWork.Shown)
                .Select(absence => new AbsenceRow(
                    absence.Id,
                    absence.Resource.UserId,
                    (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Trim(),
                    absence.Kind,
                    absence.State,
                    absence.FirstDay,
                    absence.StartTime,
                    absence.LastDay,
                    absence.EndTime))
                .ToListAsync(cancellationToken);
            return Result<OwnAbsenceStatus>.Success(new OwnAbsenceStatus(today.Value, rows, count, waiting));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the own absence of user {UserId} failed.", self);
            return Result<OwnAbsenceStatus>.Failure(ReadFailed);
        }
    }

    public async Task<Result<AbsenceImpact>> ReadAbsenceImpactAsync(Guid absenceId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<AbsenceImpact>.Failure(NoImpactView);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<AbsenceImpact>.Failure(ImpactNotOnConsole);

        if (session.TenantId is not { } tenantId)
            return Result<AbsenceImpact>.Failure(IRuntimeExecutionContext.NoTenant);

        var zone = await this.clock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<AbsenceImpact>.Failure(zone.Error ?? ReadFailed);

        var now = await this.clock.NowAsync(cancellationToken);
        if (!now.IsSuccess)
            return Result<AbsenceImpact>.Failure(now.Error ?? ReadFailed);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var absence = await context.ResourceAbsences.AsNoTracking()
                .Where(absence => absence.TenantId == tenantId && absence.Id == absenceId)
                .Select(absence => new
                {
                    Scheduled = new ScheduledAbsence(absence.Id, absence.ResourceId, absence.Kind, absence.State, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime),
                    Name = (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Trim(),
                    absence.Resource.User.Email,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (absence is null)
                return Result<AbsenceImpact>.Failure(NotFound);

            var scheduled = absence.Scheduled;
            var rows = AbsenceRules.ScheduledStates.Contains(scheduled.State)
                ? (await AbsenceImpactReader.ReadAsync(context, tenantId, [scheduled], now.Value.UtcDateTime, timeZone, withTakenOff: true, cancellationToken))[scheduled.AbsenceId]
                : [];
            var staff = absence.Name.Length > 0 ? absence.Name : absence.Email ?? StaffNotFound;
            return Result<AbsenceImpact>.Success(new AbsenceImpact(scheduled.AbsenceId, staff, scheduled.Kind, scheduled.State, AbsenceRules.PeriodText(scheduled.FirstDay, scheduled.StartTime, scheduled.LastDay, scheduled.EndTime), rows));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading what absence {AbsenceId} affects failed.", absenceId);
            return Result<AbsenceImpact>.Failure(ReadFailed);
        }
    }

    private async Task<Result<ListPage<AbsenceRow>>> ReadPageAsync(Guid tenantId, Guid? ofUser, ListQuery list, string statusKey, string kindKey, bool byStaff, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var absences = context.ResourceAbsences.AsNoTracking().Where(absence => absence.TenantId == tenantId);
            if (ofUser is { } user)
            {
                absences = absences.Where(absence => absence.Resource.UserId == user);
            }

            if (list.Search is { Length: > 0 } search)
            {
                absences = absences.Where(absence => (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Contains(search) || (absence.Resource.User.Email != null && absence.Resource.User.Email.Contains(search)));
            }

            if (list.Filter(statusKey) is { } status && ListOption.TryParseEnum<ResourceAbsenceState>(status, out var state))
            {
                absences = absences.Where(absence => absence.State == state);
            }

            if (list.Filter(kindKey) is { } kindText && ListOption.TryParseEnum<AbsenceKind>(kindText, out var kind))
            {
                absences = absences.Where(absence => absence.Kind == kind);
            }

            var total = await absences.CountAsync(cancellationToken);
            var page = ListPage<AbsenceRow>.PageWithin(list, total);
            var order = new ListOrder<ResourceAbsence>(absences);
            _ = byStaff
                ? order.By(absence => absence.Resource.User.FirstName, list.Descending).By(absence => absence.Resource.User.LastName, list.Descending).By(absence => absence.FirstDay, true)
                : order.By(absence => absence.FirstDay, list.Descending).By(absence => absence.Resource.User.FirstName).By(absence => absence.Resource.User.LastName);
            var rows = await order.By(absence => absence.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(absence => new AbsenceRow(
                    absence.Id,
                    absence.Resource.UserId,
                    (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Trim(),
                    absence.Kind,
                    absence.State,
                    absence.FirstDay,
                    absence.StartTime,
                    absence.LastDay,
                    absence.EndTime))
                .ToListAsync(cancellationToken);
            return Result<ListPage<AbsenceRow>>.Success(ListPage<AbsenceRow>.Ungrouped(rows, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the absences of tenant {TenantId} failed.", tenantId);
            return Result<ListPage<AbsenceRow>>.Failure(ReadFailed);
        }
    }

    public async Task<bool> MayAddAbsenceForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var own = this.runtime.EffectiveUserId == userId;
        if (!await this.permissionService.HasPermissionAsync(own ? RolePermission.ReportOwnAbsence : RolePermission.ManageResourceAbsence, cancellationToken))
            return false;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Resources.AsNoTracking().Where(resource => resource.UserId == userId).AnyAsync(StaffServiceRules.InService, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<AbsenceStaffChoice>>> GetRegistrableStaffMembersAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ManageResourceAbsence, cancellationToken))
            return Result<IReadOnlyList<AbsenceStaffChoice>>.Success([]);

        var session = this.runtime.Current;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var staffMembers = await context.Resources.AsNoTracking()
                .Where(StaffServiceRules.InService)
                .Where(resource => resource.UserId != session.EffectiveUserId)
                .OrderBy(resource => resource.User.FirstName).ThenBy(resource => resource.User.LastName).ThenBy(resource => resource.UserId)
                .Select(resource => new { resource.UserId, Name = (resource.User.FirstName + " " + resource.User.LastName).Trim(), resource.User.Email })
                .ToListAsync(cancellationToken);
            return Result<IReadOnlyList<AbsenceStaffChoice>>.Success([.. staffMembers.Select(staff => new AbsenceStaffChoice(staff.UserId, staff.Name.Length > 0 ? staff.Name : staff.Email ?? "Unknown staff member"))]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the staff members an absence may be registered for failed.");
            return Result<IReadOnlyList<AbsenceStaffChoice>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<DetachedEntityUpdater<ResourceAbsence>>> LoadResourceAbsenceUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = this.runtime.Current;
        var readsAll = await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken);
        if (!readsAll && !await this.permissionService.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken))
            return Result<DetachedEntityUpdater<ResourceAbsence>>.Failure(NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var absence = await context.ResourceAbsences
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);

            return absence is null || (!readsAll && absence.Resource.UserId != session.EffectiveUserId)
                ? Result<DetachedEntityUpdater<ResourceAbsence>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<ResourceAbsence>>.Success(new DetachedEntityUpdater<ResourceAbsence>(absence, context, UpdaterOptions));
        }, this.log, $"the absence {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<ResourceAbsence>> NewResourceAbsenceAsync(Guid userId, CancellationToken cancellationToken)
    {
        var session = this.runtime.Current;
        var own = session.EffectiveUserId == userId;
        if (!await this.permissionService.HasPermissionAsync(own ? RolePermission.ReportOwnAbsence : RolePermission.ManageResourceAbsence, cancellationToken))
            return Result<ResourceAbsence>.Failure(NoReport);

        if (session.TenantId is not { } tenantId)
            return Result<ResourceAbsence>.Failure(IRuntimeExecutionContext.NoTenant);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<ResourceAbsence>.Failure(today.Error);

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var staff = await context.Resources.AsNoTracking().Include(resource => resource.User).FirstOrDefaultAsync(resource => resource.UserId == userId, cancellationToken);
        if (staff is null)
            return Result<ResourceAbsence>.Failure(StaffNotFound);

        return Result<ResourceAbsence>.Success(new ResourceAbsence
        {
            TenantId = tenantId,
            State = ResourceAbsenceState.Requested,
            ResourceId = staff.Id,
            Resource = staff,
            Kind = AbsenceKind.PlannedLeave,
            FirstDay = today.Value,
            LastDay = today.Value,
        });
    }

    public async Task<Result<Guid>> CreateResourceAbsenceAsync(ResourceAbsence absence, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(absence);
        var required = transitionKey switch
        {
            ResourceAbsenceWorkflowAttribute.ReportStep => RolePermission.ReportOwnAbsence,
            ResourceAbsenceWorkflowAttribute.RegisterStep => RolePermission.ManageResourceAbsence,
            _ => (RolePermission?)null,
        };
        if (required is not { } permission)
            return Result<Guid>.Failure(CreateByReportOrRegister);

        if (!await this.permissionService.HasPermissionAsync(permission, cancellationToken))
            return Result<Guid>.Failure(NoReport);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        if (AbsenceRules.Problems(absence.Kind, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime) is { Count: > 0 } problems)
            return Result<Guid>.Failure(string.Join(" ", problems));

        var step = transitionKey == ResourceAbsenceWorkflowAttribute.RegisterStep
            && await this.permissionService.HasPermissionAsync(RolePermission.ApproveResourceAbsence, cancellationToken)
            && !await IsOwnRecordAsync(absence.ResourceId, cancellationToken)
                ? absence.Kind == AbsenceKind.SickLeave ? ResourceAbsenceWorkflowAttribute.HandleSickStep : ResourceAbsenceWorkflowAttribute.ApproveStep
                : transitionKey;

        var row = new ResourceAbsence
        {
            TenantId = tenantId,
            State = ResourceAbsenceState.Requested,
            ResourceId = absence.ResourceId,
            Kind = absence.Kind,
            FirstDay = absence.FirstDay,
            StartTime = absence.StartTime,
            LastDay = absence.LastDay,
            EndTime = absence.EndTime,
        };

        try
        {
            return await this.workflows.CreateAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(row, step, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Creating an absence in tenant {TenantId} failed.", tenantId);
            return Result<Guid>.Failure("An error occurred while saving the absence.");
        }
    }

    public async Task<Result> UpdateResourceAbsenceAndTransitionAsync(DetachedEntityUpdater<ResourceAbsence> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasAnyPermissionAsync([RolePermission.ReportOwnAbsence, RolePermission.ManageResourceAbsence, RolePermission.ApproveResourceAbsence], cancellationToken))
            return Result.Failure(NoChange);

        var id = (Guid)updater.OriginalValue(nameof(ResourceAbsence.Id))!;

        if (WorkflowRegistry.Default.Get<ResourceAbsenceState, ResourceAbsence>().TryGetTransition(transitionKey, out var step) && step.TakesNoEdits && updater.HasChanges())
            return Result.Failure(TransitionRefusals.PendingEdits);

        var edited = updater.Entity;
        if (AbsenceRules.Problems(edited.Kind, edited.FirstDay, edited.StartTime, edited.LastDay, edited.EndTime) is { Count: > 0 } problems)
            return Result.Failure(string.Join(" ", problems));

        try
        {
            return await this.workflows.SaveAndTransitionAsync<ResourceAbsenceState, ResourceAbsence>(updater, transitionKey, ResourceAbsenceWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the absence {AbsenceId} failed.", id);
            return Result.Failure("An error occurred while saving the absence.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetResourceAbsenceHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await MayReadAsync(id, cancellationToken) is { } refusal
            ? Result<StateHistoryPage>.Failure(refusal)
            : await this.history.GetHistoryAsync<ResourceAbsence>(id, query, cancellationToken);

    public async Task<Result<IReadOnlyList<CommentThread>>> GetResourceAbsenceCommentsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await MayReadAsync(id, cancellationToken) is { } refusal)
            return Result<IReadOnlyList<CommentThread>>.Failure(refusal);

        var access = await IsOwnAbsenceAsync(id, cancellationToken) && await this.permissionService.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken) ? OwnThread : ApproverThread;
        var read = await this.comments.GetThreadsAsync<ResourceAbsence>(id, [access], cancellationToken);
        return read.IsSuccess && read.Value is { } threads
            ? Result<IReadOnlyList<CommentThread>>.Success([.. threads.Where(thread => thread.TotalCount > 0)])
            : read;
    }

    public async Task<Result<CommentThread>> AddResourceAbsenceCommentAsync(Guid id, CommentAudience audience, string text, CancellationToken cancellationToken)
    {
        if (audience != CommentAudience.Staff)
            return Result<CommentThread>.Failure(CommentService.NoAccess);

        if (await MayReadAsync(id, cancellationToken) is { } refusal)
            return Result<CommentThread>.Failure(refusal);

        var access = await IsOwnAbsenceAsync(id, cancellationToken) && await this.permissionService.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken) ? OwnThread : ApproverThread;
        return await this.comments.AddCommentAsync<ResourceAbsence>(id, access, text, cancellationToken);
    }

    public async Task<Result<AbsenceEditorOptions>> GetResourceAbsenceEditorOptionsAsync(Guid? id, Guid? userId, CancellationToken cancellationToken)
    {
        var session = this.runtime.Current;
        if (id is { } stored && await MayReadAsync(stored, cancellationToken) is { } refusal)
            return Result<AbsenceEditorOptions>.Failure(refusal);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var staff = id is { } absenceId
                ? await context.ResourceAbsences.AsNoTracking().Where(absence => absence.Id == absenceId)
                    .Select(absence => new { absence.Resource.UserId, absence.Resource.User.FirstName, absence.Resource.User.LastName, absence.State })
                    .FirstOrDefaultAsync(cancellationToken)
                : userId is { } user
                    ? await context.Resources.AsNoTracking().Where(resource => resource.UserId == user)
                        .Select(resource => new { resource.UserId, resource.User.FirstName, resource.User.LastName, State = ResourceAbsenceState.Requested })
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
            if (staff is null)
                return Result<AbsenceEditorOptions>.Failure(id is null ? StaffNotFound : NotFound);

            var own = staff.UserId == session.EffectiveUserId;
            var name = $"{staff.FirstName} {staff.LastName}".Trim() is { Length: > 0 } full ? full : "Unknown staff member";
            var manages = await this.permissionService.HasPermissionAsync(RolePermission.ManageResourceAbsence, cancellationToken);
            var reportsOwn = own && await this.permissionService.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken);
            if (id is null && !(own ? reportsOwn : manages))
                return Result<AbsenceEditorOptions>.Failure(NoReport);

            var approves = await this.permissionService.HasPermissionAsync(RolePermission.ApproveResourceAbsence, cancellationToken);
            var note = id is null
                ? own
                    ? "You report your own absence: a sick leave keeps you from being staffed at once, a planned leave once an approver approves it."
                    : approves
                        ? $"Registered on {name}'s behalf, it is approved at once, since you approve absence. {name} is told."
                        : $"Registered on {name}'s behalf, it waits for an approver. A sick leave keeps them from being staffed at once. {name} is told."
                : !approves && !reportsOwn && staff.State is ResourceAbsenceState.Approved or ResourceAbsenceState.Rejected ? ReasonWithApprovers : null;

            var mayEdit = staff.State == ResourceAbsenceState.Question ? reportsOwn : manages || reportsOwn;
            return Result<AbsenceEditorOptions>.Success(new AbsenceEditorOptions(FieldsLocked: !mayEdit, name, note));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the options of the absence {AbsenceId} failed.", id);
            return Result<AbsenceEditorOptions>.Failure(ReadFailed);
        }
    }

    private async Task<bool> IsOwnAbsenceAsync(Guid absenceId, CancellationToken cancellationToken)
    {
        var self = this.runtime.EffectiveUserId;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.ResourceAbsences.AsNoTracking().AnyAsync(absence => absence.Id == absenceId && absence.Resource.UserId == self, cancellationToken);
    }

    private async Task<bool> IsOwnRecordAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var self = this.runtime.EffectiveUserId;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Resources.AsNoTracking().AnyAsync(resource => resource.Id == resourceId && resource.UserId == self, cancellationToken);
    }

    private async Task<string?> MayReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = this.runtime.Current;
        var readsAll = await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken);
        if (!readsAll && !await this.permissionService.HasPermissionAsync(RolePermission.ReportOwnAbsence, cancellationToken))
            return NoView;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var owner = await context.ResourceAbsences.AsNoTracking().Where(absence => absence.Id == id).Select(absence => (Guid?)absence.Resource.UserId).FirstOrDefaultAsync(cancellationToken);
        return owner is null || (!readsAll && owner != session.EffectiveUserId) ? NotFound : null;
    }
}

public sealed record AbsenceStaffChoice(Guid UserId, string Name);
