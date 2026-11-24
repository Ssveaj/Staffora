using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
using Staffora.Services.Settings;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Scheduling;

public sealed class PatternAssignmentService
{
    public const int MaxStaffPerAssign = 50;

    public static readonly string StaffOutOfRange = $"Choose 1 to {MaxStaffPerAssign} staff members.";

    public const string PatternNotFound = "Work pattern not found.";

    public const string AssignInOwnTenantOnly = "The platform console gives no tenant's staff a work pattern: impersonate one of the tenant's users to do it.";

    public const string NothingAssigned = "Nothing was assigned.";

    public static readonly IReadOnlyList<RolePermission> Readers = [RolePermission.ResourcesView, RolePermission.AssignWorkPatterns];

    public const string NoView = "You do not have permission to view the work patterns given to staff members.";

    public const string NoAssign = "You do not have permission to assign work patterns.";

    public const string NotFound = "Pattern assignment not found.";

    public const string StaffNotFound = "Staff member not found.";

    public const string ReadFailed = "The work patterns given to the staff member could not be read. Try again.";

    public const string CreateBySaveOnly = "A work pattern is given by saving a prepared assignment. It is activated afterwards, as a step of its own.";

    public const string ArchivedPatternNote = "This pattern is archived: the assignment keeps it as it is, and nobody new is given it.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly TenantClock clock;
    private readonly ILogger<PatternAssignmentService> log;

    public PatternAssignmentService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        TenantClock clock,
        ILogger<PatternAssignmentService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.clock = clock;
        this.log = log;
    }

    private static DetachedEntityUpdaterOptions<ResourcePatternAssignment> UpdaterOptions => new();

    public async Task<Result<ListPage<PatternAssignmentRow>>> QueryAssignmentsOfUserAsync(Guid userId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<PatternAssignmentRow>>.Failure(NoView);

        var list = StaffScheduleList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var assignments = context.ResourcePatternAssignments.AsNoTracking().Where(assignment => assignment.Resource.UserId == userId);
            if (list.Search is { Length: > 0 } search)
            {
                assignments = assignments.Where(assignment => assignment.WorkPattern.Name.Contains(search));
            }

            if (list.Filter(StaffScheduleList.Status) is { } status && ListOption.TryParseEnum<ResourcePatternAssignmentState>(status, out var state))
            {
                assignments = assignments.Where(assignment => assignment.State == state);
            }

            var total = await assignments.CountAsync(cancellationToken);
            var page = ListPage<PatternAssignmentRow>.PageWithin(list, total);
            var order = new ListOrder<ResourcePatternAssignment>(assignments);
            _ = list.Sort == StaffScheduleList.SortPattern
                ? order.By(assignment => assignment.WorkPattern.Name, list.Descending).By(assignment => assignment.EffectiveFrom, true)
                : order.By(assignment => assignment.EffectiveFrom, list.Descending).By(assignment => assignment.WorkPattern.Name);
            var rows = await order.By(assignment => assignment.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(assignment => new PatternAssignmentRow(assignment.Id, assignment.WorkPattern.Name, assignment.WorkPattern.State, assignment.EffectiveFrom, assignment.EffectiveTo, assignment.State))
                .ToListAsync(cancellationToken);
            return Result<ListPage<PatternAssignmentRow>>.Success(ListPage<PatternAssignmentRow>.Ungrouped(rows, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the pattern assignments of user {UserId} failed.", userId);
            return Result<ListPage<PatternAssignmentRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<ListPage<PatternStaffRow>>> QueryAssignmentsOfPatternAsync(Guid patternId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<PatternStaffRow>>.Failure(NoView);

        var list = WorkPatternStaffList.Definition.Normalize(query);
        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<ListPage<PatternStaffRow>>.Failure(today.Error);

        var day = today.Value;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var assignments = context.ResourcePatternAssignments.AsNoTracking()
                .Where(assignment => assignment.WorkPatternId == patternId && assignment.State != ResourcePatternAssignmentState.Deleted);
            if (list.Search is { Length: > 0 } search)
            {
                assignments = assignments.Where(assignment => (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Contains(search)
                    || (assignment.Resource.User.Email != null && assignment.Resource.User.Email.Contains(search)));
            }

            assignments = list.Filter(WorkPatternStaffList.When) switch
            {
                WorkPatternStaffList.WhenCurrent => assignments.Where(assignment => assignment.EffectiveFrom <= day && (assignment.EffectiveTo == null || assignment.EffectiveTo >= day)),
                WorkPatternStaffList.WhenUpcoming => assignments.Where(assignment => assignment.EffectiveFrom > day),
                WorkPatternStaffList.WhenEnded => assignments.Where(assignment => assignment.EffectiveTo != null && assignment.EffectiveTo < day),
                _ => assignments,
            };

            if (list.Filter(WorkPatternStaffList.Status) is { } status && ListOption.TryParseEnum<ResourcePatternAssignmentState>(status, out var state))
            {
                assignments = assignments.Where(assignment => assignment.State == state);
            }

            var total = await assignments.CountAsync(cancellationToken);
            var page = ListPage<PatternStaffRow>.PageWithin(list, total);
            var order = new ListOrder<ResourcePatternAssignment>(assignments);
            _ = list.Sort == WorkPatternStaffList.SortStaff
                ? order.By(assignment => assignment.Resource.User.FirstName, list.Descending).By(assignment => assignment.Resource.User.LastName, list.Descending).By(assignment => assignment.EffectiveFrom, true)
                : order.By(assignment => assignment.EffectiveFrom, list.Descending).By(assignment => assignment.Resource.User.FirstName).By(assignment => assignment.Resource.User.LastName);
            var rows = await order.By(assignment => assignment.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(assignment => new
                {
                    assignment.Id,
                    assignment.Resource.UserId,
                    Name = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(),
                    assignment.Resource.User.Email,
                    assignment.EffectiveFrom,
                    assignment.EffectiveTo,
                    assignment.State,
                })
                .ToListAsync(cancellationToken);
            List<PatternStaffRow> items = [.. rows.Select(row => new PatternStaffRow(row.Id, row.UserId, row.Name.Length > 0 ? row.Name : row.Email ?? "Unknown staff member", row.EffectiveFrom, row.EffectiveTo, row.State, day))];
            return Result<ListPage<PatternStaffRow>>.Success(ListPage<PatternStaffRow>.Ungrouped(items, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the assignments of work pattern {PatternId} failed.", patternId);
            return Result<ListPage<PatternStaffRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<PatternStaffChoices>> GetStaffChoicesForPatternAsync(Guid patternId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken))
            return Result<PatternStaffChoices>.Failure(NoAssign);

        if (this.runtime.Current.HasPlatformReach())
            return Result<PatternStaffChoices>.Failure(AssignInOwnTenantOnly);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<PatternStaffChoices>.Failure(IRuntimeExecutionContext.NoTenant);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<PatternStaffChoices>.Failure(today.Error);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var pattern = await context.WorkPatterns.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.Id == patternId)
                .Select(row => new { row.Name, row.State })
                .FirstOrDefaultAsync(cancellationToken);
            if (pattern is null)
                return Result<PatternStaffChoices>.Failure(PatternNotFound);

            var staff = await context.Resources.AsNoTracking()
                .Where(resource => resource.TenantId == tenantId)
                .Where(StaffServiceRules.SetUpOpen)
                .OrderBy(resource => resource.User.FirstName).ThenBy(resource => resource.User.LastName).ThenBy(resource => resource.Id)
                .Select(resource => new { resource.Id, Name = (resource.User.FirstName + " " + resource.User.LastName).Trim(), resource.User.Email })
                .ToListAsync(cancellationToken);
            var covers = await StaffPatternCoverage.CoversAsync(context, [.. staff.Select(row => row.Id)], today.Value, cancellationToken);

            var holders = (await context.ResourcePatternAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId && assignment.WorkPatternId == patternId)
                .Where(assignment => assignment.State == ResourcePatternAssignmentState.Registration || assignment.State == ResourcePatternAssignmentState.Active)
                .Where(assignment => assignment.EffectiveTo == null || assignment.EffectiveTo >= today.Value)
                .Select(assignment => assignment.ResourceId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();
            return Result<PatternStaffChoices>.Success(new PatternStaffChoices(
                patternId,
                pattern.Name,
                pattern.State == WorkPatternState.Published,
                [.. staff.Select(row => new PatternStaffChoice(row.Id, row.Name.Length > 0 ? row.Name : row.Email ?? "Unknown staff member", row.Email ?? string.Empty, covers.GetValueOrDefault(row.Id), holders.Contains(row.Id)))],
                today.Value));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the staff to give work pattern {PatternId} failed.", patternId);
            return Result<PatternStaffChoices>.Failure(ReadFailed);
        }
    }

    public async Task<Result<PatternStaffAssignment>> AssignPatternToStaffAsync(Guid patternId, IReadOnlyCollection<Guid> resourceIds, DateOnly from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken))
            return Result<PatternStaffAssignment>.Failure(NoAssign);

        if (this.runtime.Current.HasPlatformReach())
            return Result<PatternStaffAssignment>.Failure(AssignInOwnTenantOnly);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<PatternStaffAssignment>.Failure(IRuntimeExecutionContext.NoTenant);

        var chosen = resourceIds.Distinct().ToList();
        if (chosen.Count is 0 or > MaxStaffPerAssign)
            return Result<PatternStaffAssignment>.Failure(StaffOutOfRange);

        var anchor = ScheduleRules.MondayOf(from);
        if (ScheduleRules.Problems(from, to, anchor) is { Count: > 0 } problems)
            return Result<PatternStaffAssignment>.Failure(string.Join(" ", problems));

        try
        {
            List<PatternStaffRefusal> refused = [];
            string pattern;
            await using (var context = this.dbFactory.CreateDbContext(this.runtime))
            {
                var stored = await context.WorkPatterns.AsNoTracking()
                    .Where(row => row.TenantId == tenantId && row.Id == patternId)
                    .Select(row => new { row.Name, row.State })
                    .FirstOrDefaultAsync(cancellationToken);
                if (stored is null)
                    return Result<PatternStaffAssignment>.Failure(PatternNotFound);

                if (stored.State != WorkPatternState.Published)
                    return Result<PatternStaffAssignment>.Failure($"{ResourcePatternAssignmentWorkflowAttribute.PatternPublishedReason}.");

                pattern = stored.Name;
                var staff = await context.Resources.AsNoTracking()
                    .Where(resource => resource.TenantId == tenantId && chosen.Contains(resource.Id))
                    .Select(resource => new { resource.Id, resource.State, Name = (resource.User.FirstName + " " + resource.User.LastName).Trim() })
                    .ToDictionaryAsync(resource => resource.Id, cancellationToken);
                var active = (await context.ResourcePatternAssignments.AsNoTracking()
                        .Where(assignment => assignment.TenantId == tenantId && chosen.Contains(assignment.ResourceId) && assignment.State == ResourcePatternAssignmentState.Active)
                        .Select(assignment => new { assignment.ResourceId, assignment.Id, assignment.EffectiveFrom, assignment.EffectiveTo, assignment.WorkPattern.Name })
                        .ToListAsync(cancellationToken))
                    .ToLookup(assignment => assignment.ResourceId, assignment => new ScheduledDates(assignment.Id, assignment.EffectiveFrom, assignment.EffectiveTo, assignment.Name));
                var candidate = new ScheduledDates(Guid.Empty, from, to);
                foreach (var resourceId in chosen)
                {
                    if (!staff.TryGetValue(resourceId, out var member))
                    {
                        refused.Add(new PatternStaffRefusal(resourceId, "Unknown staff member", StaffNotFound));
                        continue;
                    }

                    var name = member.Name.Length > 0 ? member.Name : "A staff member";
                    if (!StaffServiceRules.IsSetUpOpen(member.State))
                    {
                        refused.Add(new PatternStaffRefusal(resourceId, name, $"{ResourcePatternAssignmentWorkflowAttribute.StaffSetUpOpenReason}."));
                    }
                    else if (ScheduleRules.OverlapReasons(candidate, active[resourceId]) is { Count: > 0 } overlaps)
                    {
                        refused.Add(new PatternStaffRefusal(resourceId, name, string.Join(" ", overlaps)));
                    }
                }
            }

            if (refused.Count > 0)
            {
                return new Result<PatternStaffAssignment>
                {
                    IsSuccess = false,
                    Error = $"{NothingAssigned} {string.Join(" ", refused.Select(refusal => refusal.Label))}",
                    Value = new PatternStaffAssignment([], refused),
                };
            }

            return await this.workflows.RunBatchAsync<PatternStaffAssignment>($"work pattern {patternId} given to {chosen.Count} staff members", async (batch, token) =>
            {
                var ids = new List<Guid>();
                foreach (var resourceId in chosen)
                {
                    var created = await batch.CreateAsync<ResourcePatternAssignmentState, ResourcePatternAssignment>(
                        new ResourcePatternAssignment
                        {
                            TenantId = tenantId,
                            State = ResourcePatternAssignmentState.Registration,
                            ResourceId = resourceId,
                            WorkPatternId = patternId,
                            EffectiveFrom = from,
                            EffectiveTo = to,
                            CycleAnchor = anchor,
                        },
                        ResourcePatternAssignmentWorkflowAttribute.SaveStep,
                        ResourcePatternAssignmentWorkflowQueries.WithWorkflowGraph,
                        token);
                    var activated = created.IsSuccess
                        ? await batch.TransitionAsync<ResourcePatternAssignmentState, ResourcePatternAssignment>(created.Value, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, ResourcePatternAssignmentWorkflowQueries.WithWorkflowGraph, token)
                        : created;
                    if (!activated.IsSuccess)
                    {
                        var refusal = new PatternStaffRefusal(resourceId, await NameOfAsync(resourceId, token), activated.Error ?? "The step was refused.");
                        return new Result<PatternStaffAssignment>
                        {
                            IsSuccess = false,
                            Error = $"{NothingAssigned} {refusal.Label}",
                            Value = new PatternStaffAssignment([], [refusal]),
                        };
                    }

                    ids.Add(created.Value);
                }

                return Result<PatternStaffAssignment>.Success(new PatternStaffAssignment(ids, []), $"{pattern} is given to {chosen.Count} staff {(chosen.Count == 1 ? "member" : "members")}.");
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Giving work pattern {PatternId} to {Count} staff members failed.", patternId, chosen.Count);
            return Result<PatternStaffAssignment>.Failure("An error occurred while assigning the work pattern.");
        }
    }

    private async Task<string> NameOfAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var name = await context.Resources.AsNoTracking()
            .Where(resource => resource.Id == resourceId)
            .Select(resource => (resource.User.FirstName + " " + resource.User.LastName).Trim())
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? "A staff member" : name;
    }

    public async Task<bool> MayAssignToUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken))
            return false;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Resources.AsNoTracking().Where(resource => resource.UserId == userId).AnyAsync(StaffServiceRules.SetUpOpen, cancellationToken);
    }

    public async Task<Result<DetachedEntityUpdater<ResourcePatternAssignment>>> LoadResourcePatternAssignmentUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<DetachedEntityUpdater<ResourcePatternAssignment>>.Failure(NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var assignment = await context.ResourcePatternAssignments
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return assignment is null
                ? Result<DetachedEntityUpdater<ResourcePatternAssignment>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<ResourcePatternAssignment>>.Success(new DetachedEntityUpdater<ResourcePatternAssignment>(assignment, context, UpdaterOptions));
        }, this.log, $"the pattern assignment {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<ResourcePatternAssignment>> NewResourcePatternAssignmentAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken))
            return Result<ResourcePatternAssignment>.Failure(NoAssign);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ResourcePatternAssignment>.Failure(IRuntimeExecutionContext.NoTenant);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<ResourcePatternAssignment>.Failure(today.Error);

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var staff = await context.Resources.AsNoTracking().FirstOrDefaultAsync(resource => resource.UserId == userId, cancellationToken);
        if (staff is null)
            return Result<ResourcePatternAssignment>.Failure(StaffNotFound);

        return Result<ResourcePatternAssignment>.Success(new ResourcePatternAssignment
        {
            TenantId = tenantId,
            State = ResourcePatternAssignmentState.Registration,
            ResourceId = staff.Id,
            Resource = staff,
            EffectiveFrom = today.Value,
            CycleAnchor = ScheduleRules.MondayOf(today.Value),
        });
    }

    public async Task<Result<Guid>> CreateResourcePatternAssignmentAsync(ResourcePatternAssignment assignment, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken))
            return Result<Guid>.Failure(NoAssign);

        if (transitionKey != ResourcePatternAssignmentWorkflowAttribute.SaveStep)
            return Result<Guid>.Failure(CreateBySaveOnly);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        var row = new ResourcePatternAssignment
        {
            TenantId = tenantId,
            State = ResourcePatternAssignmentState.Registration,
            ResourceId = assignment.ResourceId,
            WorkPatternId = assignment.WorkPatternId,
            EffectiveFrom = assignment.EffectiveFrom,
            EffectiveTo = assignment.EffectiveTo,
            CycleAnchor = assignment.CycleAnchor,
        };

        try
        {
            return await this.workflows.CreateAndTransitionAsync<ResourcePatternAssignmentState, ResourcePatternAssignment>(row, transitionKey, ResourcePatternAssignmentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Creating a pattern assignment in tenant {TenantId} failed.", tenantId);
            return Result<Guid>.Failure("An error occurred while assigning the work pattern.");
        }
    }

    public async Task<Result> UpdateResourcePatternAssignmentAndTransitionAsync(DetachedEntityUpdater<ResourcePatternAssignment> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken))
            return Result.Failure(NoAssign);

        var id = (Guid)updater.OriginalValue(nameof(ResourcePatternAssignment.Id))!;
        try
        {
            if (WorkflowRegistry.Default.Get<ResourcePatternAssignmentState, ResourcePatternAssignment>().IsDeleteStep(transitionKey))
            {
                return await this.workflows.TransitionEntityAsync<ResourcePatternAssignmentState, ResourcePatternAssignment>(id, transitionKey, ResourcePatternAssignmentWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            }

            var edited = updater.Entity;
            if (ScheduleRules.Problems(edited.EffectiveFrom, edited.EffectiveTo, edited.CycleAnchor) is { Count: > 0 } problems)
            {
                return Result.Failure(string.Join(" ", problems));
            }

            return await this.workflows.SaveAndTransitionAsync<ResourcePatternAssignmentState, ResourcePatternAssignment>(updater, transitionKey, ResourcePatternAssignmentWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the pattern assignment {AssignmentId} failed.", id);
            return Result.Failure("An error occurred while saving the pattern assignment.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetResourcePatternAssignmentHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<ResourcePatternAssignment>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoView);

    public async Task<Result<PatternAssignmentEditorOptions>> GetResourcePatternAssignmentEditorOptionsAsync(Guid? id, Guid? userId, CancellationToken cancellationToken)
    {
        var assigns = await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken);
        if (id is null ? !assigns : !(assigns || await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)))
            return Result<PatternAssignmentEditorOptions>.Failure(id is null ? NoAssign : NoView);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var stored = id is { } assignmentId
                ? await context.ResourcePatternAssignments.AsNoTracking()
                    .Where(assignment => assignment.Id == assignmentId)
                    .Select(assignment => new { assignment.WorkPatternId, PatternState = assignment.WorkPattern.State, assignment.Resource.UserId })
                    .FirstOrDefaultAsync(cancellationToken)
                : null;
            if (id is not null && stored is null)
                return Result<PatternAssignmentEditorOptions>.Failure(NotFound);

            var staffUser = stored?.UserId ?? userId;
            var staff = staffUser is { } user
                ? await context.Users.AsNoTracking().Where(row => row.Id == user).Select(row => (row.FirstName + " " + row.LastName).Trim()).FirstOrDefaultAsync(cancellationToken)
                : null;
            var own = stored?.WorkPatternId;
            var patterns = await context.WorkPatterns.AsNoTracking()
                .Where(pattern => pattern.State == WorkPatternState.Published || pattern.Id == own)
                .OrderBy(pattern => pattern.Name).ThenBy(pattern => pattern.Id)
                .ToListAsync(cancellationToken);
            return Result<PatternAssignmentEditorOptions>.Success(new PatternAssignmentEditorOptions(
                FieldsLocked: !assigns,
                Staff: string.IsNullOrWhiteSpace(staff) ? "Unknown staff member" : staff,
                Patterns: patterns,
                PatternNote: stored?.PatternState == WorkPatternState.Archived ? ArchivedPatternNote : null));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the options of the pattern assignment {AssignmentId} failed.", id);
            return Result<PatternAssignmentEditorOptions>.Failure(ReadFailed);
        }
    }
}

public sealed record PatternAssignmentEditorOptions(bool FieldsLocked, string Staff, IReadOnlyList<WorkPattern> Patterns, string? PatternNote);

public sealed record PatternStaffAssignment(IReadOnlyList<Guid> Created, IReadOnlyList<PatternStaffRefusal> Refused);
