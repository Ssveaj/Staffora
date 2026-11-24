using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Settings;

namespace Staffora.Services.Jobs;

public sealed class StaffingService
{
    public const string NoView = StaffAvailabilityService.NoViewCoordination;

    public const string ReadFailed = "The candidates could not be read. Try again.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly IStaffAvailability availability;
    private readonly QualificationService qualifications;
    private readonly TenantClock tenantClock;
    private readonly TimeProvider clock;
    private readonly ILogger<StaffingService> log;

    public StaffingService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, IStaffAvailability availability, QualificationService qualifications, TenantClock tenantClock, TimeProvider clock, ILogger<StaffingService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.availability = availability;
        this.qualifications = qualifications;
        this.tenantClock = tenantClock;
        this.clock = clock;
        this.log = log;
    }

    public async Task<Result<ListPage<StaffingCandidateRow>>> QuerySlotCandidatesAsync(Guid slotId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<ListPage<StaffingCandidateRow>>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<ListPage<StaffingCandidateRow>>.Failure(JobSlotService.NotFound);

        if (session.TenantId is not { } tenantId)
            return Result<ListPage<StaffingCandidateRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = StaffingCandidateList.Definition.Normalize(query);
        try
        {
            var ranked = await RankAsync(tenantId, slotId, cancellationToken);
            if (!ranked.IsSuccess || ranked.Value is not { } rows)
                return Result<ListPage<StaffingCandidateRow>>.Failure(ranked.Error);

            IEnumerable<StaffingCandidateRow> shown = rows;
            if (list.Search is { Length: > 0 } search)
            {
                shown = shown.Where(row => row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
            }

            shown = list.Filter(StaffingCandidateList.Availability) switch
            {
                StaffingCandidateList.Free => shown.Where(row => !row.Verdict.IsBlocked),
                StaffingCandidateList.Blocked => shown.Where(row => row.Verdict.IsBlocked),
                _ => shown,
            };
            shown = list.Sort == StaffingCandidateList.SortName
                ? list.Descending
                    ? shown.OrderByDescending(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Rank)
                    : shown.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Rank)
                : list.Descending ? shown.OrderByDescending(row => row.Rank) : shown.OrderBy(row => row.Rank);

            var all = shown.ToList();
            var page = ListPage<StaffingCandidateRow>.PageWithin(list, all.Count);
            return Result<ListPage<StaffingCandidateRow>>.Success(ListPage<StaffingCandidateRow>.Ungrouped([.. all.Skip((page - 1) * list.PageSize).Take(list.PageSize)], all.Count, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the candidates of slot {SlotId} failed.", slotId);
            return Result<ListPage<StaffingCandidateRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<RoundCandidates>> ReadRoundCandidatesAsync(Guid slotId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<RoundCandidates>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<RoundCandidates>.Failure(JobSlotService.NotFound);

        if (session.TenantId is not { } tenantId)
            return Result<RoundCandidates>.Failure(IRuntimeExecutionContext.NoTenant);

        try
        {
            var ranked = await RankAsync(tenantId, slotId, cancellationToken);
            if (!ranked.IsSuccess || ranked.Value is not { } rows)
                return Result<RoundCandidates>.Failure(ranked.Error);

            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var group = await SlotGroupStateAsync(context, slotId, cancellationToken);
            var assigned = await context.SlotAssignments.AsNoTracking().CountAsync(assignment => group.SlotIds.Contains(assignment.JobSlotId) && assignment.State == SlotAssignmentState.Assigned, cancellationToken);
            return Result<RoundCandidates>.Success(new RoundCandidates(rows, Math.Max(0, group.Count - assigned)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the round candidates of slot {SlotId} failed.", slotId);
            return Result<RoundCandidates>.Failure(ReadFailed);
        }
    }

    internal async Task<SlotStaffingSummary?> SummaryOfSlotAsync(ApplicationDbContext context, Guid slotId, bool mayStaff, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken) || this.runtime.Current.HasPlatformReach())
            return null;

        var slot = await context.JobSlots.AsNoTracking()
            .AsSplitQuery()
            .Where(row => row.Id == slotId)
            .OrderBy(row => row.Id)
            .Select(row => new
            {
                row.State,
                Job = row.Job.Name,
                JobState = row.Job.State,
                row.Job.Schedule,
                row.Job.PeriodStart,
                row.Job.PeriodEnd,
                row.Job.StartTime,
                row.Job.EndTime,
                Occurrences = row.Job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
                Required = row.Qualifications.Select(required => new { required.Qualification.Name, Archived = required.Qualification.State == QualificationState.Archived, required.Level }).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (slot is null)
            return null;

        var group = await SlotGroupStateAsync(context, slotId, cancellationToken);
        var holders = (await context.SlotAssignments.AsNoTracking()
                .Where(assignment => group.SlotIds.Contains(assignment.JobSlotId) && (assignment.State == SlotAssignmentState.Assigned || assignment.State == SlotAssignmentState.Accepted))
                .OrderBy(assignment => assignment.Created)
                .ThenBy(assignment => assignment.Id)
                .Select(assignment => new { assignment.Id, assignment.State, Name = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(), assignment.Resource.User.Email })
                .ToListAsync(cancellationToken))
            .Select(assignment => new SlotHolder(assignment.Id, assignment.Name.Length > 0 ? assignment.Name : assignment.Email ?? "Unknown staff member", assignment.State == SlotAssignmentState.Assigned))
            .OrderByDescending(holder => holder.Assigned)
            .ToList();
        var openAgain = this.runtime.TenantId is { } tenantId
            ? (await OpenAgainReader.ReadAsync(context, tenantId, [group.SlotIds], cancellationToken)).CountFor(group.SlotIds, Math.Max(0, group.Count - holders.Count(holder => holder.Assigned)))
            : 0;

        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return null;

        var asked = (await context.SlotAssignments.AsNoTracking()
                .Where(assignment => group.SlotIds.Contains(assignment.JobSlotId) && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion))
                .OrderBy(assignment => assignment.RespondBy == null)
                .ThenBy(assignment => assignment.RespondBy)
                .ThenBy(assignment => assignment.Created)
                .ThenBy(assignment => assignment.Id)
                .Select(assignment => new { assignment.Id, assignment.State, assignment.RespondBy, Name = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(), assignment.Resource.User.Email })
                .ToListAsync(cancellationToken))
            .Select(assignment => new SlotAsked(assignment.Id, assignment.Name.Length > 0 ? assignment.Name : assignment.Email ?? "Unknown staff member", SlotAssignmentService.AnswerByText(assignment.State, assignment.RespondBy, timeZone)))
            .ToList();

        var occurrences = slot.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End)).OrderBy(occurrence => occurrence.Weekday).ThenBy(occurrence => occurrence.Start).ToList();
        var times = new JobTimes(slot.Schedule, slot.PeriodStart, slot.PeriodEnd, slot.StartTime, slot.EndTime, occurrences);
        var remaining = StaffingRules.RemainingOccurrences(times, this.clock.GetUtcNow().UtcDateTime, timeZone).Count;
        var reason = mayStaff ? null
            : slot.State != JobSlotState.Active || !JobRules.StaffingOpen(slot.JobState) ? SlotAssignmentService.NotStaffable
            : group.Full ? group.Count > 1 ? SlotAssignmentService.GroupFull : SlotAssignmentService.SlotHeld
            : SlotAssignmentService.NoStaffing;
        return new SlotStaffingSummary(
            slot.Job,
            JobRules.WhenText(times),
            times.IsSingle ? "Once" : occurrences.Count == 0 ? "No times" : string.Join(", ", occurrences.Select(occurrence => $"{occurrence.Weekday.ToString()[..3]} {JobRules.Times(occurrence)}")),
            remaining,
            [.. slot.Required.OrderBy(required => required.Name, StringComparer.CurrentCultureIgnoreCase).Select(required => (required.Archived ? $"{required.Name} (archived)" : required.Name) + (required.Level == RequirementLevel.Preferred ? " (preferred)" : string.Empty))],
            mayStaff,
            reason)
        {
            SlotCount = group.Count,
            Held = group.Held,
            Full = group.Full,
            Holders = holders,
            Asked = asked,
            OpenAgain = openAgain,
            Qualifications = [.. slot.Required
                .OrderBy(required => required.Level == RequirementLevel.Preferred)
                .ThenBy(required => required.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(required => new RequiredQualification(required.Name, required.Level == RequirementLevel.Preferred, required.Archived))],
        };
    }

    internal static async Task<(IReadOnlyList<Guid> SlotIds, int Count, int Held, bool Full)> SlotGroupStateAsync(ApplicationDbContext context, Guid slotId, CancellationToken cancellationToken)
    {
        var read = await SlotGroupReader.GroupOfAsync(context, slotId, cancellationToken);
        IReadOnlyList<Guid> ids = read is { } found ? found.Group.SlotIds : [slotId];
        var states = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => ids.Contains(assignment.JobSlotId) && (assignment.State == SlotAssignmentState.Assigned || assignment.State == SlotAssignmentState.Accepted))
            .Select(assignment => assignment.State)
            .ToListAsync(cancellationToken);
        var assigned = states.Count(state => state == SlotAssignmentState.Assigned);
        return (ids, ids.Count, SlotGroups.Held(ids.Count, assigned, states.Count - assigned), assigned >= ids.Count);
    }

    private async Task<Result<IReadOnlyList<StaffingCandidateRow>>> RankAsync(Guid tenantId, Guid slotId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);

        var slot = await context.JobSlots.AsNoTracking()
            .AsSplitQuery()
            .Where(row => row.Id == slotId && row.TenantId == tenantId)
            .OrderBy(row => row.Id)
            .Select(row => new
            {
                row.Job.Schedule,
                row.JobId,
                row.Job.PeriodStart,
                row.Job.PeriodEnd,
                row.Job.StartTime,
                row.Job.EndTime,
                Occurrences = row.Job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
                Required = row.Qualifications.Select(required => new { required.QualificationId, required.Qualification.Name, required.Level }).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (slot is null)
            return Result<IReadOnlyList<StaffingCandidateRow>>.Failure(JobSlotService.NotFound);

        var jobSlots = await SlotGroupReader.ReadAsync(context, [slot.JobId], cancellationToken);
        IReadOnlyList<Guid> groupIds = SlotGroups.GroupOf(slotId, jobSlots)?.SlotIds ?? [slotId];
        var assignments = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => groupIds.Contains(assignment.JobSlotId))
            .Select(assignment => new { assignment.ResourceId, assignment.State, assignment.Created })
            .ToListAsync(cancellationToken);

        var live = assignments.Where(assignment => SlotAssignmentWorkflowAttribute.IsLive(assignment.State)).Select(assignment => assignment.ResourceId).ToHashSet();
        var staffMembers = (await context.Resources.AsNoTracking()
                .Where(resource => resource.TenantId == tenantId)
                .Where(StaffServiceRules.InService)
                .Select(resource => new { resource.Id, Name = (resource.User.FirstName + " " + resource.User.LastName).Trim(), resource.User.Email })
                .ToListAsync(cancellationToken))
            .Where(staff => !live.Contains(staff.Id))
            .Select(staff => (staff.Id, Name: staff.Name.Length > 0 ? staff.Name : staff.Email ?? "Unknown staff member"))
            .ToList();
        if (staffMembers.Count == 0)
            return Result<IReadOnlyList<StaffingCandidateRow>>.Success([]);

        var ids = staffMembers.Select(staff => staff.Id).ToList();

        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<IReadOnlyList<StaffingCandidateRow>>.Failure(zone.Error);

        var now = this.clock.GetUtcNow().UtcDateTime;
        var times = new JobTimes(slot.Schedule, slot.PeriodStart, slot.PeriodEnd, slot.StartTime, slot.EndTime, [.. slot.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))]);
        var occurrences = StaffingRules.RemainingOccurrences(times, now, timeZone);

        IReadOnlyDictionary<Guid, StaffAvailability> available = new Dictionary<Guid, StaffAvailability>();
        if (occurrences.Count > 0 && StaffingRules.RemainingRange(slot.PeriodStart, slot.PeriodEnd, now, timeZone) is { } range)
        {
            var answer = await this.availability.ReadAsync(ids, range.From, range.To, cancellationToken);
            if (!answer.IsSuccess || answer.Value is null)
                return Result<IReadOnlyList<StaffingCandidateRow>>.Failure(answer.Error);

            available = answer.Value.StaffMembers;
        }

        var held = await this.qualifications.ReadQualificationsOfStaffMembersAsync(ids, cancellationToken);
        if (!held.IsSuccess || held.Value is null)
            return Result<IReadOnlyList<StaffingCandidateRow>>.Failure(held.Error);

        var bookings = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId
                && ids.Contains(assignment.ResourceId)
                && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned)
                && assignment.Slot.State == JobSlotState.Active
                && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress)
                && assignment.Slot.Job.PeriodStart <= slot.PeriodEnd
                && assignment.Slot.Job.PeriodEnd >= slot.PeriodStart)
            .Select(assignment => new { assignment.Id, assignment.ResourceId, assignment.State, assignment.Slot.JobId, Job = assignment.Slot.Job.Name, Slot = assignment.Slot.Name })
            .ToListAsync(cancellationToken);
        var heldJobs = bookings.Where(booking => SlotAssignmentWorkflowAttribute.Holds(booking.State))
            .GroupBy(booking => booking.ResourceId)
            .ToDictionary(group => group.Key, group => group.Select(booking => booking.JobId).Distinct().Count());
        var bookedOn = bookings.ToDictionary(booking => booking.Id, booking => $"{booking.Job}, {booking.Slot}");
        var bookedLine = bookings.ToDictionary(booking => booking.Id, booking => $"{booking.Job} · {booking.Slot}");
        var earlier = assignments.Where(assignment => !SlotAssignmentWorkflowAttribute.IsLive(assignment.State))
            .GroupBy(assignment => assignment.ResourceId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(assignment => assignment.Created).First().State);
        var required = slot.Required.Select(qualification => new SlotRequirement(qualification.QualificationId, qualification.Level)).ToList();
        var names = slot.Required.ToDictionary(qualification => qualification.QualificationId, qualification => qualification.Name);
        var asked = slot.Required
            .GroupBy(qualification => qualification.QualificationId)
            .Select(group => (Id: group.Key, group.First().Name, Preferred: group.All(qualification => qualification.Level == RequirementLevel.Preferred)))
            .OrderBy(qualification => qualification.Preferred)
            .ThenBy(qualification => qualification.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var judged = staffMembers.Select(staff =>
        {
            var verdict = occurrences.Count == 0
                ? StaffingVerdict.None
                : StaffingRules.Judge(available.TryGetValue(staff.Id, out var found) ? found : new StaffAvailability(staff.Id, [], [], []), occurrences);
            var own = held.Value.TryGetValue(staff.Id, out var theirs) ? theirs : new HashSet<Guid>();
            var match = StaffingRules.MatchAtLevels(required, own);
            return new
            {
                staff.Id,
                staff.Name,
                Verdict = verdict,
                Match = match,
                Marks = asked.Select(qualification => new QualificationMark(qualification.Name, qualification.Preferred, own.Contains(qualification.Id))).ToList(),
                Held = heldJobs.GetValueOrDefault(staff.Id),
                Earlier = earlier.TryGetValue(staff.Id, out var state) ? state : (SlotAssignmentState?)null,
            };
        });
        var ranked = StaffingRules.Rank(judged, row => new RankFacts(row.Id, row.Name, row.Match.MeetsAll, row.Match.Matching, row.Held, row.Match.PreferredMatching));
        return Result<IReadOnlyList<StaffingCandidateRow>>.Success([.. ranked.Select((row, index) => new StaffingCandidateRow(
            slotId,
            row.Id,
            index + 1,
            row.Name,
            row.Match,
            [.. row.Match.Missing.Select(id => names[id]).Order(StringComparer.CurrentCultureIgnoreCase)],
            row.Held,
            row.Verdict,
            [.. row.Verdict.BlockGroups.Select(group => group.TextAbout(About(group.SourceId)))],
            [.. row.Verdict.WarningGroups.Select(group => group.TextAbout(About(group.SourceId)))],
            row.Earlier)
        {
            BlockLines = [.. row.Verdict.BlockGroups.Select(group => group.LineAbout(Line(group.SourceId)))],
            WarningLines = [.. row.Verdict.WarningGroups.Select(group => group.LineAbout(Line(group.SourceId)))],
            Marks = row.Marks,
        })]);

        string? About(Guid? source) => source is { } id && bookedOn.TryGetValue(id, out var on) ? on : null;

        string? Line(Guid? source) => source is { } id && bookedLine.TryGetValue(id, out var on) ? on : null;
    }
}

public sealed record RoundCandidates(IReadOnlyList<StaffingCandidateRow> Candidates, int OpenSlots);
