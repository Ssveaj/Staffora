using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed class RequestRoundService
{
    public const int MaxCandidates = 30;

    public const string NoView = "You do not have permission to view request rounds.";

    public const string NoSend = "You do not have permission to send or close request rounds.";

    public const string NotFound = "Request round not found.";

    public static readonly string CandidatesOutOfRange = $"A request round asks 1 to {MaxCandidates} staff members.";

    public const string ReadFailed = "The request rounds could not be read. Try again.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly TenantSettingsService settings;
    private readonly TimeProvider clock;
    private readonly ILogger<RequestRoundService> log;

    public RequestRoundService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, WorkflowService workflows, StateHistoryService history, TenantSettingsService settings, TimeProvider clock, ILogger<RequestRoundService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.settings = settings;
        this.clock = clock;
        this.log = log;
    }

    public Task<Result<Guid>> SendRoundAsync(Guid slotId, IReadOnlyList<Guid> resourceIds, bool firstToAccept, CancellationToken cancellationToken = default) =>
        SendRoundAsync(slotId, resourceIds, firstToAccept, null, cancellationToken);

    public async Task<Result<Guid>> SendRoundAsync(Guid slotId, IReadOnlyList<Guid> resourceIds, bool firstToAccept, ResponseWindow? within, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.PerformCoordination, cancellationToken))
            return Result<Guid>.Failure(NoSend);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Guid>.Failure(JobService.ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        var asked = resourceIds.Distinct().ToList();
        if (asked.Count is 0 or > MaxCandidates)
            return Result<Guid>.Failure(CandidatesOutOfRange);

        try
        {
            Dictionary<Guid, string> names;
            await using (var context = this.dbFactory.CreateDbContext(this.runtime))
            {
                names = await context.Resources.AsNoTracking()
                    .Where(resource => asked.Contains(resource.Id))
                    .Select(resource => new { resource.Id, Name = (resource.User.FirstName + " " + resource.User.LastName).Trim() })
                    .ToDictionaryAsync(resource => resource.Id, resource => resource.Name, cancellationToken);
            }

            var respondBy = await SlotAssignmentService.RespondByAsync(this.settings, this.clock, within, cancellationToken);
            if (!respondBy.IsSuccess)
                return Result<Guid>.Failure(respondBy.Error);

            return await this.workflows.RunBatchAsync<Guid>("a request round", async (batch, token) =>
            {
                var round = await batch.CreateAsync<RequestRoundState, RequestRound>(
                    new RequestRound { TenantId = tenantId, State = RequestRoundState.Create, JobSlotId = slotId, FirstToAccept = firstToAccept, RespondBy = respondBy.Value },
                    RequestRoundWorkflowAttribute.SendStep,
                    RequestRoundWorkflowQueries.WithWorkflowGraph,
                    token);
                if (!round.IsSuccess)
                {
                    return Result<Guid>.Failure(round.Error);
                }

                foreach (var resourceId in asked)
                {
                    var request = await batch.CreateAsync<SlotAssignmentState, SlotAssignment>(
                        new SlotAssignment { TenantId = tenantId, State = SlotAssignmentState.Create, JobSlotId = slotId, ResourceId = resourceId, RequestRoundId = round.Value, RespondBy = respondBy.Value },
                        SlotAssignmentWorkflowAttribute.RequestStep,
                        SlotAssignmentWorkflowQueries.WithWorkflowGraph,
                        token);
                    if (!request.IsSuccess)
                    {
                        var name = names.TryGetValue(resourceId, out var known) && known.Length > 0 ? known : "A staff member";
                        return Result<Guid>.Failure($"{name} cannot be asked: {request.Error} Nothing was sent.");
                    }
                }

                return Result<Guid>.Success(round.Value, $"The round asks {asked.Count} staff {(asked.Count == 1 ? "member" : "members")}.");
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Sending a request round from slot {SlotId} failed.", slotId);
            return Result<Guid>.Failure("An error occurred while sending the request round.");
        }
    }

    public async Task<Result<ListPage<RequestRoundRow>>> QueryGroupRoundsAsync(Guid slotId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<ListPage<RequestRoundRow>>.Failure(NoView);

        var list = RequestRoundList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var groupIds = (await SlotGroupReader.GroupOfAsync(context, slotId, cancellationToken))?.Group.SlotIds ?? [slotId];
            var rounds = context.RequestRounds.AsNoTracking().Where(round => groupIds.Contains(round.JobSlotId));
            if (list.Filter(RequestRoundList.Status) is { } status && ListOption.TryParseEnum<RequestRoundState>(status, out var state))
            {
                rounds = rounds.Where(round => round.State == state);
            }

            var total = await rounds.CountAsync(cancellationToken);
            var page = ListPage<RequestRoundRow>.PageWithin(list, total);
            var ordered = list.Descending ? rounds.OrderByDescending(round => round.Created).ThenBy(round => round.Id) : rounds.OrderBy(round => round.Created).ThenBy(round => round.Id);
            var found = await ordered
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(round => new
                {
                    round.Id,
                    round.State,
                    round.FirstToAccept,
                    round.Created,
                    round.CreatedById,
                    Requested = round.Requests.Count(request => request.State == SlotAssignmentState.Requested || request.State == SlotAssignmentState.StaffQuestion),
                    Accepted = round.Requests.Count(request => request.State == SlotAssignmentState.Accepted),
                    Assigned = round.Requests.Count(request => request.State == SlotAssignmentState.Assigned),
                    Closed = round.Requests.Count(request => request.State == SlotAssignmentState.Declined || request.State == SlotAssignmentState.NotSelected || request.State == SlotAssignmentState.Canceled || request.State == SlotAssignmentState.Expired),
                })
                .ToListAsync(cancellationToken);
            var senders = await SendersAsync(context, found.Select(round => round.CreatedById), cancellationToken);
            return Result<ListPage<RequestRoundRow>>.Success(ListPage<RequestRoundRow>.Ungrouped(
                [.. found.Select(round => new RequestRoundRow(round.Id, round.State, round.FirstToAccept, round.Created, senders.GetValueOrDefault(round.CreatedById, "A coordinator"), round.Requested, round.Accepted, round.Assigned, round.Closed))],
                total,
                page,
                list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the request rounds of slot {SlotId} failed.", slotId);
            return Result<ListPage<RequestRoundRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<ListPage<SlotAssignmentRow>>> QueryRoundRequestsAsync(Guid roundId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<ListPage<SlotAssignmentRow>>.Failure(NoView);

        var list = SlotAssignmentList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var rows = context.SlotAssignments.AsNoTracking().Where(assignment => assignment.RequestRoundId == roundId);
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
                : order.By(assignment => assignment.Resource.User.FirstName, list.Descending).By(assignment => assignment.Resource.User.LastName, list.Descending);
            var found = await order.By(assignment => assignment.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(assignment => new SlotAssignmentRow(assignment.Id, assignment.JobSlotId, (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(), assignment.State, assignment.Created))
                .ToListAsync(cancellationToken);
            return Result<ListPage<SlotAssignmentRow>>.Success(ListPage<SlotAssignmentRow>.Ungrouped(found, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the requests of round {RoundId} failed.", roundId);
            return Result<ListPage<SlotAssignmentRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<DetachedEntityUpdater<RequestRound>>> LoadRequestRoundUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<DetachedEntityUpdater<RequestRound>>.Failure(NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);

            var round = await context.RequestRounds
                .WithWorkflowGraph()
                .AsNoTrackingWithIdentityResolution()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return round is null
                ? Result<DetachedEntityUpdater<RequestRound>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<RequestRound>>.Success(new DetachedEntityUpdater<RequestRound>(round, context));
        }, this.log, $"the request round {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result> UpdateRequestRoundAndTransitionAsync(DetachedEntityUpdater<RequestRound> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.PerformCoordination, cancellationToken))
            return Result.Failure(NoSend);

        if (this.runtime.Current.HasPlatformReach())
            return Result.Failure(JobService.ChangeInOwnTenantOnly);

        if (updater.HasChanges())
            return Result.Failure(TransitionRefusals.PendingEdits);

        var id = (Guid)updater.OriginalValue(nameof(RequestRound.Id))!;
        try
        {
            return await this.workflows.SaveAndTransitionAsync<RequestRoundState, RequestRound>(updater, transitionKey, RequestRoundWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Taking a step on the request round {RoundId} failed.", id);
            return Result.Failure("An error occurred while saving the request round.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetRequestRoundHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken)
            ? await this.history.GetHistoryAsync<RequestRound>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoView);

    public async Task<Result<RequestRoundSummary>> GetRequestRoundEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<RequestRoundSummary>.Failure(NoView);

        if (id is not { } roundId)
            return Result<RequestRoundSummary>.Failure(NotFound);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var round = await context.RequestRounds.AsNoTracking()
                .Where(row => row.Id == roundId)
                .Select(row => new
                {
                    row.JobSlotId,
                    Job = row.Slot.Job.Name,
                    Slot = row.Slot.Name,
                    row.FirstToAccept,
                    row.Created,
                    row.CreatedById,
                    Requested = row.Requests.Count(request => request.State == SlotAssignmentState.Requested || request.State == SlotAssignmentState.StaffQuestion),
                    Accepted = row.Requests.Count(request => request.State == SlotAssignmentState.Accepted),
                    Assigned = row.Requests.Count(request => request.State == SlotAssignmentState.Assigned),
                    Closed = row.Requests.Count(request => request.State == SlotAssignmentState.Declined || request.State == SlotAssignmentState.NotSelected || request.State == SlotAssignmentState.Canceled || request.State == SlotAssignmentState.Expired),
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (round is null)
                return Result<RequestRoundSummary>.Failure(NotFound);

            var group = await StaffingService.SlotGroupStateAsync(context, round.JobSlotId, cancellationToken);
            var senders = await SendersAsync(context, [round.CreatedById], cancellationToken);
            return Result<RequestRoundSummary>.Success(new RequestRoundSummary(
                round.Job,
                round.Slot,
                group.Count,
                group.Held,
                round.FirstToAccept,
                senders.GetValueOrDefault(round.CreatedById, "A coordinator"),
                round.Created,
                round.Requested,
                round.Accepted,
                round.Assigned,
                round.Closed));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the request round {RoundId} failed.", roundId);
            return Result<RequestRoundSummary>.Failure(ReadFailed);
        }
    }

    private static async Task<Dictionary<Guid, string>> SendersAsync(ApplicationDbContext context, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return await context.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, Name = (user.FirstName + " " + user.LastName).Trim() })
            .ToDictionaryAsync(user => user.Id, user => user.Name, cancellationToken);
    }
}
