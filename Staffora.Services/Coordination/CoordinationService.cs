using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Core.Entities.Catalogue;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Diagnostics;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Settings;

namespace Staffora.Services.Coordination;

public sealed partial class CoordinationService
{
    public const string NoView = StaffAvailabilityService.NoViewCoordination;

    public const string NotOnConsole = "The platform console keeps no organization's coordination. Impersonate one of its users to see it.";

    public const string ReadFailed = "The coordination could not be read. Try again.";

    public const int MaxBehind = 50;

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly IStaffAvailability availability;
    private readonly QualificationService qualifications;
    private readonly TenantClock tenantClock;
    private readonly JobClock jobClock;
    private readonly TimeProvider clock;
    private readonly ILogger<CoordinationService> log;

    public CoordinationService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, IStaffAvailability availability, QualificationService qualifications, TenantClock tenantClock, JobClock jobClock, TimeProvider clock, ILogger<CoordinationService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.availability = availability;
        this.qualifications = qualifications;
        this.tenantClock = tenantClock;
        this.jobClock = jobClock;
        this.clock = clock;
        this.log = log;
    }

    public async Task<Result<CoordinationSnapshot>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<CoordinationSnapshot>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<CoordinationSnapshot>.Failure(NotOnConsole);

        if (session.TenantId is not { } tenantId)
            return Result<CoordinationSnapshot>.Failure(IRuntimeExecutionContext.NoTenant);

        try
        {
            return await ReadTenantAsync(tenantId, cancellationToken);
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "the coordination workspace", cancellationToken);
            this.log.LogError(exception, "Reading the coordination of tenant {TenantId} failed.", tenantId);
            return Result<CoordinationSnapshot>.Failure(ReadFailed);
        }
    }

    private async Task<Result<CoordinationSnapshot>> ReadTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<CoordinationSnapshot>.Failure(zone.Error);

        var instant = this.clock.GetUtcNow();
        var now = instant.UtcDateTime;
        var today = JobRules.LocalDay(instant, timeZone);
        await using var context = this.dbFactory.CreateDbContext(this.runtime);

        var jobs = await context.Jobs.AsNoTracking()
            .AsSplitQuery()
            .Where(job => job.TenantId == tenantId
                && (job.State == JobState.Coordinating || job.State == JobState.Confirmed || job.State == JobState.InProgress)
                && job.PeriodEnd >= today)
            .OrderBy(job => job.Id)
            .Select(job => new
            {
                job.Id,
                job.Name,
                Customer = job.Customer.Name,
                job.CustomerId,
                job.OrganizationUnitId,
                job.State,
                job.PlaceName,
                job.City,
                job.Schedule,
                job.PeriodStart,
                job.PeriodEnd,
                job.StartTime,
                job.EndTime,
                Occurrences = job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var slots = await context.JobSlots.AsNoTracking()
            .Where(slot => slot.TenantId == tenantId
                && slot.State == JobSlotState.Active
                && (slot.Job.State == JobState.Coordinating || slot.Job.State == JobState.Confirmed || slot.Job.State == JobState.InProgress)
                && slot.Job.PeriodEnd >= today)
            .OrderBy(slot => slot.Id)
            .Select(slot => new { slot.Id, slot.JobId, slot.Name, slot.Created, slot.ServiceId, Requirements = slot.Qualifications.Select(required => new { required.QualificationId, required.Level }).ToList() })
            .ToListAsync(cancellationToken);

        var assignments = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId
                && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned)
                && assignment.Slot.State == JobSlotState.Active
                && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress)
                && assignment.Slot.Job.PeriodEnd >= today)
            .Select(assignment => new
            {
                assignment.Id,
                assignment.JobSlotId,
                assignment.ResourceId,
                assignment.State,
                Name = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(),
                assignment.Resource.User.Email,
                assignment.LastStaffCommentAt,
                assignment.LastCoordinatorCommentAt,
            })
            .ToListAsync(cancellationToken);

        var expiredOf = (await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId
                    && assignment.State == SlotAssignmentState.Expired
                    && assignment.Slot.State == JobSlotState.Active
                    && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress)
                    && assignment.Slot.Job.PeriodEnd >= today)
                .GroupBy(assignment => assignment.JobSlotId)
                .Select(expired => new { SlotId = expired.Key, Count = expired.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(expired => expired.SlotId, expired => expired.Count);

        var timesOf = jobs.ToDictionary(
            job => job.Id,
            job => new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, [.. job.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))]));
        var occurrencesOf = jobs.ToDictionary(
            job => job.Id,
            job => StaffingRules.RemainingOccurrences(timesOf[job.Id], now, timeZone));

        var holders = assignments.Where(assignment => assignment.State == SlotAssignmentState.Assigned).Select(assignment => assignment.ResourceId).Distinct().ToList();
        IReadOnlyDictionary<Guid, StaffAvailability> available = new Dictionary<Guid, StaffAvailability>();
        var lastDay = occurrencesOf.Values.SelectMany(occurrences => occurrences).Select(occurrence => occurrence.EndDate).DefaultIfEmpty(today).Max();
        var readTo = lastDay > today.AddDays(StaffAvailabilityService.MaxDays - 1) ? today.AddDays(StaffAvailabilityService.MaxDays - 1) : lastDay;
        if (holders.Count > 0)
        {
            var answer = await this.availability.ReadAsync(holders, today, readTo, cancellationToken);
            if (!answer.IsSuccess || answer.Value is null)
                return Result<CoordinationSnapshot>.Failure(answer.Error);

            available = answer.Value.StaffMembers;
        }

        var behind = (await context.Jobs.AsNoTracking()
                .Where(job => job.TenantId == tenantId
                    && (((job.State == JobState.Coordinating || job.State == JobState.Confirmed) && job.PeriodStart <= today)
                        || (job.State == JobState.InProgress && job.PeriodEnd <= today)))
                .OrderBy(job => job.PeriodStart)
                .ThenBy(job => job.Id)
                .Select(job => new { job.Id, job.Name, job.State, job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime })
                .Take(MaxBehind)
                .ToListAsync(cancellationToken))
            .Where(job => job.State == JobState.InProgress
                ? JobRules.HasEnded(new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, []), now, timeZone)
                : JobRules.HasStarted(new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, []), now, timeZone))
            .ToList();

        var jobOf = jobs.ToDictionary(job => job.Id);
        var liveOf = assignments.ToLookup(assignment => assignment.JobSlotId);
        var slotOf = slots.ToDictionary(slot => slot.Id);
        var entries = new List<SlotEntry>();
        var groups = SlotGroups.Of(slots.Select(slot => new SlotGroupInput(slot.Id, slot.JobId, JobSlotState.Active, slot.Name, slot.ServiceId, [.. slot.Requirements.Select(required => new SlotRequirement(required.QualificationId, required.Level))], slot.Created)));
        var openAgain = await OpenAgainReader.ReadAsync(context, tenantId, [.. groups.Select(group => (IReadOnlyCollection<Guid>)group.SlotIds)], cancellationToken);

        foreach (var group in groups)
        {
            var slot = slotOf[group.Representative];
            if (!jobOf.TryGetValue(slot.JobId, out var job) || occurrencesOf[slot.JobId] is not { Count: > 0 } occurrences)
            {
                continue;
            }

            var live = group.SlotIds.SelectMany(id => liveOf[id]).ToList();
            var groupHolders = live.Where(assignment => assignment.State == SlotAssignmentState.Assigned).Select(assigned =>
            {
                var theirs = available.TryGetValue(assigned.ResourceId, out var found) ? found : new StaffAvailability(assigned.ResourceId, [], [], []);
                return new HolderFacts(assigned.ResourceId, NameOf(assigned.Name, assigned.Email), StaffingRules.Judge(theirs, occurrences, assigned.Id));
            }).OrderBy(holder => holder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var holder = groupHolders.FirstOrDefault(found => found.Verdict.IsBlocked) ?? groupHolders.FirstOrDefault();

            var awaiting = live
                .Where(assignment => StaffConversation.AwaitsCoordinatorReply(assignment.LastStaffCommentAt, assignment.LastCoordinatorCommentAt))
                .Select(assignment => NameOf(assignment.Name, assignment.Email))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var facts = new SlotFacts(occurrences, JobRules.HasStarted(timesOf[job.Id], now, timeZone), holder, live.Count(assignment => assignment.State == SlotAssignmentState.Accepted), live.Count(assignment => SlotAssignmentWorkflowAttribute.IsAsked(assignment.State)), awaiting)
            {
                Count = group.Count,
                Holders = groupHolders,
                Expired = group.SlotIds.Sum(id => expiredOf.GetValueOrDefault(id)),
            };
            var next = PriorityRules.NextOccurrenceUtc(facts);
            var row = new CoordinationSlotRow(
                slot.Id,
                slot.Name,
                job.Id,
                job.Name,
                job.Customer,
                job.State,
                job.PeriodStart,
                job.PeriodEnd,
                PriorityRules.Classify(facts),
                PriorityRules.Reasons(facts),
                next,
                PriorityRules.StartsLabel(next, facts.PeriodStarted, now),
                StaffMembersLabel(holder, facts))
            {
                Times = timesOf[job.Id],
                SlotCount = group.Count,
                Assigned = Math.Min(group.Count, groupHolders.Count),
                Requested = facts.Requested,
                Accepted = facts.Accepted,
                AtRisk = groupHolders.Count(found => found.Verdict.IsBlocked),
                OpenAgain = openAgain.CountFor(group.SlotIds, Math.Max(0, group.Count - groupHolders.Count)),
            };
            entries.Add(new SlotEntry(
                row,
                [.. occurrences.SelectMany(JobRules.DaySegments).Select(segment => segment.Date).Distinct()],
                [.. groupHolders.SelectMany(found => found.Verdict.Blocks).SelectMany(finding => JobRules.DaySegments(finding.Occurrence)).Select(segment => segment.Date).Distinct()],
                occurrences,
                live.Select(assignment => assignment.ResourceId).ToHashSet(),
                job.OrganizationUnitId,
                job.CustomerId,
                [.. slot.Requirements.Where(required => required.Level == RequirementLevel.Required).Select(required => required.QualificationId)],
                JobPlace.ShortTextOf(job.PlaceName, job.City)));
        }

        var ordered = entries
            .OrderBy(entry => entry.Row.NextOccurrenceUtc ?? DateTime.MaxValue)
            .ThenBy(entry => entry.Row.Job, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Row.Slot, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Row.SlotId)
            .ToList();
        var late = behind.Select(job => new JobBehind(job.Id, job.Name, job.State, BehindReason(tenantId, job.Id, job.State, job.PeriodStart, job.PeriodEnd, timeZone))).ToList();
        return Result<CoordinationSnapshot>.Success(new CoordinationSnapshot(tenantId, ordered, late, today, now, timeZone.Id));
    }

    private static string StaffMembersLabel(HolderFacts? holder, SlotFacts facts)
    {
        if (facts.Count > 1)
        {
            var held = SlotGroups.HeldText(SlotGroups.Held(facts.Count, facts.Holders.Count, facts.Accepted), facts.Count);
            return facts.Holders.Count > 0 ? $"{held}: {string.Join(", ", facts.Holders.Select(found => found.Name))}" : $"{held}: {StaffMembersLabel(null, facts with { Count = 1, Holders = [] })}";
        }

        if (holder is not null)
        {
            return holder.Name;
        }

        var asked = facts.Accepted + facts.Requested;
        return asked == 0 ? "Nobody asked"
            : facts.Accepted == 0 ? $"{asked} asked"
            : $"{asked} asked, {facts.Accepted} said yes";
    }

    private string BehindReason(Guid tenantId, Guid jobId, JobState state, DateOnly periodStart, DateOnly periodEnd, TimeZoneInfo zone)
    {
        var reason = state switch
        {
            JobState.Coordinating => $"Its period started on {ScheduleRules.DateText(periodStart)} and it is still being coordinated: confirm it once it is staffed, or cancel it.",
            JobState.Confirmed => $"Its period started on {ScheduleRules.DateText(periodStart)}, but it is still confirmed: the job clock has not put it in progress yet.",
            _ => $"Its period ended on {ScheduleRules.DateText(periodEnd)}, but it is still in progress: the job clock has not completed it yet.",
        };
        if (state == JobState.Coordinating || this.jobClock.FailureOf(tenantId, jobId) is not { } failure)
        {
            return reason;
        }

        var times = failure.Failures == 1 ? "once" : $"{failure.Failures} times in a row";
        var retry = TimeZoneInfo.ConvertTime(failure.NextTry, zone);
        var why = failure.Refusal is { } refusal ? $": {refusal}" : " because of an error, which the platform's error log keeps";
        return $"{reason} It could not move it {times}{why}. It tries again from {ScheduleRules.DateText(DateOnly.FromDateTime(retry.DateTime))} {retry.ToString("HH':'mm", CultureInfo.InvariantCulture)}, or as soon as the job is saved.";
    }

    private static string NameOf(string name, string? email) => name.Length > 0 ? name : email ?? "Unknown staff member";
}
