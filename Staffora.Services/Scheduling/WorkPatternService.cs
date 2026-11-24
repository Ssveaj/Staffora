using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;
using Staffora.Services.Settings;

namespace Staffora.Services.Scheduling;

public sealed class WorkPatternService
{
    public static readonly IReadOnlyList<RolePermission> Readers = [RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns];

    public const string NoView = "You do not have permission to view work patterns.";

    public const string NoManage = "You do not have permission to manage work patterns.";

    public const string NotFound = "Work pattern not found.";

    public const string ReadFailed = "The work patterns could not be read. Try again.";

    public const string CopySuffix = " (copy)";

    public static readonly NameAndDescriptionRule TextRule = new("work pattern", WorkPattern.NameMaxLength, WorkPattern.DescriptionMaxLength);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly TenantClock clock;
    private readonly ILogger<WorkPatternService> log;

    public WorkPatternService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        TenantClock clock,
        PatternAssignmentService assignments,
        ILogger<WorkPatternService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.clock = clock;
        Assignments = assignments;
        this.log = log;
    }

    public PatternAssignmentService Assignments { get; }

    private static DetachedEntityUpdaterOptions<WorkPattern> UpdaterOptions => new DetachedEntityUpdaterOptions<WorkPattern>()
        .WithCollection(pattern => pattern.Segments);

    public async Task<Result<ListPage<WorkPatternListItem>>> QueryWorkPatternsAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<WorkPatternListItem>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<WorkPatternListItem>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = WorkPatternList.Definition.Normalize(query);
        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<ListPage<WorkPatternListItem>>.Failure(today.Error);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var patterns = context.WorkPatterns.AsNoTracking().Where(pattern => pattern.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                patterns = patterns.Where(pattern => pattern.Name.Contains(search) || (pattern.Description != null && pattern.Description.Contains(search)));
            }

            if (list.Filter(WorkPatternList.Status) is { } status && ListOption.TryParseEnum<WorkPatternState>(status, out var state))
            {
                patterns = patterns.Where(pattern => pattern.State == state);
            }

            var total = await patterns.CountAsync(cancellationToken);
            var page = ListPage<WorkPatternListItem>.PageWithin(list, total);
            var order = new ListOrder<WorkPattern>(patterns);
            _ = list.Sort == WorkPatternList.SortCycle
                ? order.By(pattern => pattern.CycleWeeks, list.Descending).By(pattern => pattern.Name)
                : order.By(pattern => pattern.Name, list.Descending);
            var rows = await order.By(pattern => pattern.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(pattern => new { pattern.Id, pattern.Name, pattern.Description, pattern.CycleWeeks, pattern.State })
                .ToListAsync(cancellationToken);
            var ids = rows.Select(row => row.Id).ToList();
            var segments = (await context.WorkPatternSegments.AsNoTracking()
                    .Where(segment => segment.TenantId == tenantId && ids.Contains(segment.WorkPatternId))
                    .Select(segment => new { segment.WorkPatternId, segment.DayIndex, segment.Start, segment.End })
                    .ToListAsync(cancellationToken))
                .ToLookup(segment => segment.WorkPatternId, segment => new PatternSegment(segment.DayIndex, segment.Start, segment.End));
            var day = today.Value;
            var staff = await context.ResourcePatternAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId
                    && ids.Contains(assignment.WorkPatternId)
                    && assignment.State == ResourcePatternAssignmentState.Active
                    && (assignment.EffectiveTo == null || assignment.EffectiveTo >= day))
                .GroupBy(assignment => assignment.WorkPatternId)
                .Select(group => new
                {
                    PatternId = group.Key,
                    Today = group.Where(assignment => assignment.EffectiveFrom <= day).Select(assignment => assignment.ResourceId).Distinct().Count(),
                    Upcoming = group.Where(assignment => assignment.EffectiveFrom > day).Select(assignment => assignment.ResourceId).Distinct().Count(),
                })
                .ToDictionaryAsync(count => count.PatternId, cancellationToken);
            List<WorkPatternListItem> items = [.. rows.Select(row => new WorkPatternListItem(row.Id, row.Name, row.Description, row.CycleWeeks, row.State, segments[row.Id].Count(), WorkPatternListItem.WorkedIn(segments[row.Id]))
            {
                StaffToday = staff.TryGetValue(row.Id, out var held) ? held.Today : 0,
                StaffUpcoming = held?.Upcoming ?? 0,
            })];
            return Result<ListPage<WorkPatternListItem>>.Success(ListPage<WorkPatternListItem>.Ungrouped(items, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the work patterns of tenant {TenantId} failed.", tenantId);
            return Result<ListPage<WorkPatternListItem>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<DetachedEntityUpdater<WorkPattern>>> LoadWorkPatternUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<DetachedEntityUpdater<WorkPattern>>.Failure(NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var pattern = await context.WorkPatterns
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return pattern is null
                ? Result<DetachedEntityUpdater<WorkPattern>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<WorkPattern>>.Success(new DetachedEntityUpdater<WorkPattern>(pattern, context, UpdaterOptions));
        }, this.log, $"the work pattern {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<WorkPattern>> NewWorkPatternAsync(Guid? copyOf, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ManageWorkPatterns, cancellationToken))
            return Result<WorkPattern>.Failure(NoManage);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<WorkPattern>.Failure(IRuntimeExecutionContext.NoTenant);

        if (copyOf is not { } sourceId)
            return Result<WorkPattern>.Success(new WorkPattern { TenantId = tenantId, State = WorkPatternState.Draft, CycleWeeks = 1 });

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var source = await context.WorkPatterns.WithWorkflowGraph().AsNoTracking().FirstOrDefaultAsync(row => row.Id == sourceId, cancellationToken);
        if (source is null)
            return Result<WorkPattern>.Failure(NotFound);

        var name = source.Name.Length + CopySuffix.Length <= WorkPattern.NameMaxLength ? source.Name + CopySuffix : source.Name[..(WorkPattern.NameMaxLength - CopySuffix.Length)] + CopySuffix;
        var copy = new WorkPattern { TenantId = tenantId, State = WorkPatternState.Draft, Name = name, Description = source.Description, CycleWeeks = source.CycleWeeks };
        foreach (var segment in source.Segments.OrderBy(segment => segment.DayIndex).ThenBy(segment => segment.Start))
        {
            copy.Segments.Add(new WorkPatternSegment { TenantId = tenantId, DayIndex = segment.DayIndex, Start = segment.Start, End = segment.End });
        }

        return Result<WorkPattern>.Success(copy);
    }

    public async Task<Result<Guid>> CreateWorkPatternAsync(WorkPattern pattern, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ManageWorkPatterns, cancellationToken))
            return Result<Guid>.Failure(NoManage);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        var name = pattern.Name?.Trim() ?? string.Empty;
        var description = string.IsNullOrWhiteSpace(pattern.Description) ? null : pattern.Description.Trim();
        if (TextRule.Problem(name, description) is { } textReason)
            return Result<Guid>.Failure(textReason);

        var row = new WorkPattern { TenantId = tenantId, Name = name, Description = description, CycleWeeks = pattern.CycleWeeks, State = WorkPatternState.Draft };
        foreach (var segment in pattern.Segments)
        {
            row.Segments.Add(new WorkPatternSegment { TenantId = tenantId, DayIndex = segment.DayIndex, Start = segment.Start, End = segment.End });
        }

        try
        {
            return await this.workflows.CreateAndTransitionAsync<WorkPatternState, WorkPattern>(row, transitionKey, WorkPatternWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Creating a work pattern in tenant {TenantId} failed.", tenantId);
            return Result<Guid>.Failure("An error occurred while creating the work pattern.");
        }
    }

    public async Task<Result> UpdateWorkPatternAndTransitionAsync(DetachedEntityUpdater<WorkPattern> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ManageWorkPatterns, cancellationToken))
            return Result.Failure(NoManage);

        var id = (Guid)updater.OriginalValue(nameof(WorkPattern.Id))!;
        try
        {
            if (WorkflowRegistry.Default.Get<WorkPatternState, WorkPattern>().IsDeleteStep(transitionKey))
            {
                return await this.workflows.TransitionEntityAsync<WorkPatternState, WorkPattern>(id, transitionKey, WorkPatternWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            }

            var pattern = updater.Entity;
            if (pattern.Name is { } typed && typed != typed.Trim())
            {
                pattern.Name = typed.Trim();
            }

            if (updater.HasChanges() && TextRule.Problem(pattern.Name ?? string.Empty, pattern.Description) is { } textReason)
                return Result.Failure(textReason);

            if (updater.HasChanges() && WorkPatternRules.Problems(pattern) is { Count: > 0 } problems)
                return Result.Failure(string.Join(" ", problems));

            return await this.workflows.SaveAndTransitionAsync<WorkPatternState, WorkPattern>(updater, transitionKey, WorkPatternWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the work pattern {PatternId} failed.", id);
            return Result.Failure("An error occurred while saving the work pattern.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetWorkPatternHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<WorkPattern>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoView);

    public async Task<Result<WorkPatternEditorOptions>> GetWorkPatternEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var manages = await this.permissionService.HasPermissionAsync(RolePermission.ManageWorkPatterns, cancellationToken);
        if (id is not { } patternId)
            return manages ? Result<WorkPatternEditorOptions>.Success(new WorkPatternEditorOptions(FieldsLocked: false, MayCopy: true)) : Result<WorkPatternEditorOptions>.Failure(NoManage);

        if (!manages && !await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<WorkPatternEditorOptions>.Failure(NoView);

        var readsStaff = await this.permissionService.HasAnyPermissionAsync(PatternAssignmentService.Readers, cancellationToken);
        var published = false;
        if (await this.permissionService.HasPermissionAsync(RolePermission.AssignWorkPatterns, cancellationToken))
        {
            try
            {
                await using var context = this.dbFactory.CreateDbContext(this.runtime);
                published = await context.WorkPatterns.AsNoTracking().AnyAsync(pattern => pattern.Id == patternId && pattern.State == WorkPatternState.Published, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                this.log.LogError(exception, "Reading the options of the work pattern {PatternId} failed.", patternId);
                return Result<WorkPatternEditorOptions>.Failure(ReadFailed);
            }
        }

        return Result<WorkPatternEditorOptions>.Success(new WorkPatternEditorOptions(FieldsLocked: !manages, MayCopy: manages) { ReadsStaff = readsStaff, MayAssignStaff = published });
    }
}

public sealed record WorkPatternEditorOptions(bool FieldsLocked, bool MayCopy)
{
    public bool ReadsStaff { get; init; }

    public bool MayAssignStaff { get; init; }
}
