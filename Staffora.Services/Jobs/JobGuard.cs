using Microsoft.EntityFrameworkCore;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Data;
using Staffora.Services.Scheduling;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed class JobGuard : IJobGuard, IStepPreparation
{
    private readonly Dictionary<Guid, StoredJobReferences> stored = [];
    private readonly Dictionary<Guid, JobTimes> timetables = [];
    private readonly Dictionary<Guid, TimeZoneInfo> zones = [];
    private readonly Dictionary<Guid, TenantHolidays> holidays = [];
    private readonly Dictionary<Guid, IReadOnlyList<string>> clashes = [];
    private readonly Dictionary<Guid, string> clashesUnread = [];
    private readonly Dictionary<Guid, EditedAnswer> edited = [];
    private readonly Lock gate = new();
    private readonly TenantClock tenantClock;
    private readonly TimeProvider clock;

    public JobGuard(TenantClock tenantClock, TimeProvider clock)
    {
        this.tenantClock = tenantClock;
        this.clock = clock;
    }

    public Type EntityType => typeof(Job);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stored = await context.Jobs.AsNoTracking()
            .Where(job => job.Id == entityId)
            .Select(job => new
            {
                References = new StoredJobReferences(job.CustomerId, job.ContactId, job.OrganizationUnitId),
                job.TenantId,
                job.Schedule,
                job.PeriodStart,
                job.PeriodEnd,
                job.StartTime,
                job.EndTime,
                Occurrences = job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var zone = stored is null ? null : await this.tenantClock.GetTimeZoneOfAsync(stored.TenantId, cancellationToken);
        var holidays = stored is null ? null : await this.tenantClock.GetHolidaysOfAsync(stored.TenantId, cancellationToken);
        lock (this.gate)
        {
            if (stored is null)
            {
                this.stored.Remove(entityId);
                this.timetables.Remove(entityId);
                this.clashes.Remove(entityId);
                this.clashesUnread[entityId] = "the job was not found.";
            }
            else
            {
                this.stored[entityId] = stored.References;
                this.timetables[entityId] = new JobTimes(stored.Schedule, stored.PeriodStart, stored.PeriodEnd, stored.StartTime, stored.EndTime, [.. stored.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))]);

                this.clashes[entityId] = [];
                this.clashesUnread.Remove(entityId);
            }

            if (zone is { IsSuccess: true, Value: { } read })
            {
                this.zones[entityId] = read;
            }
            else
            {
                this.zones.Remove(entityId);
            }

            if (holidays is { IsSuccess: true, Value: { } calendar })
            {
                this.holidays[entityId] = calendar;
            }
            else
            {
                this.holidays.Remove(entityId);
            }
        }
    }

    public async Task PrepareEditedAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var edited = await context.Jobs.AsNoTracking()
            .Where(job => job.Id == entityId)
            .Select(job => new
            {
                job.TenantId,
                job.State,
                job.Schedule,
                job.PeriodStart,
                job.PeriodEnd,
                job.StartTime,
                job.EndTime,
                Occurrences = job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        JobTimes? before;
        TimeZoneInfo? zone;
        TenantHolidays? holidays;
        lock (this.gate)
        {
            before = this.timetables.GetValueOrDefault(entityId);
            zone = this.zones.GetValueOrDefault(entityId);
            holidays = this.holidays.GetValueOrDefault(entityId);
        }

        if (edited is null || before is null || zone is null || holidays is null)
        {
            Unread(entityId, edited is null ? "the job was not found." : before is null ? "its timetable as stored was not read." : zone is null ? "its tenant's time zone could not be read." : "its tenant's public holidays could not be read.");
            return;
        }

        if (!JobRules.StaffingOpen(edited.State))
        {
            Judged(entityId, []);
            return;
        }

        var now = this.clock.GetUtcNow().UtcDateTime;
        var had = StaffingRules.RemainingOccurrences(before, now, zone);
        var added = StaffingRules.RemainingOccurrences(new JobTimes(edited.Schedule, edited.PeriodStart, edited.PeriodEnd, edited.StartTime, edited.EndTime, [.. edited.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))]), now, zone)
            .Where(occurrence => !had.Any(stored => stored.StartUtc <= occurrence.StartUtc && occurrence.EndUtc <= stored.EndUtc))
            .ToList();
        if (added.Count == 0)
        {
            Judged(entityId, []);
            return;
        }

        var inService = JobCoverageReader.StaffMembersInService(context);
        var holders = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.Slot.JobId == entityId
                && assignment.Slot.State == JobSlotState.Active
                && (assignment.State == SlotAssignmentState.Assigned || assignment.State == SlotAssignmentState.Accepted)
                && inService.Contains(assignment.ResourceId))
            .Select(assignment => new { assignment.Id, assignment.ResourceId, Name = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(), assignment.Resource.User.Email })
            .ToListAsync(cancellationToken);
        if (holders.Count == 0)
        {
            Judged(entityId, []);
            return;
        }

        var own = holders.GroupBy(holder => holder.ResourceId).ToDictionary(group => group.Key, group => group.Select(holder => holder.Id).ToHashSet());
        var answer = await StaffAvailabilityService.ComputeAsync(context, edited.TenantId, [.. own.Keys], added.Min(occurrence => occurrence.Date), added.Max(occurrence => occurrence.Date), zone, holidays, cancellationToken);
        var clashes = new List<string>();
        foreach (var holder in holders.DistinctBy(holder => holder.ResourceId).OrderBy(holder => holder.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(holder => holder.ResourceId))
        {
            var mine = own[holder.ResourceId];
            var found = answer.StaffMembers.TryGetValue(holder.ResourceId, out var staff) ? staff : new StaffAvailability(holder.ResourceId, [], [], []);
            var others = found with
            {
                Blocks = [.. found.Blocks.Where(block => block.SourceId is not { } source || !mine.Contains(source))],
                Warnings = [.. found.Warnings.Where(warning => warning.SourceId is not { } source || !mine.Contains(source))],
            };
            var verdict = StaffingRules.Judge(others, added);
            if (verdict.IsBlocked)
            {
                var name = holder.Name.Length > 0 ? holder.Name : holder.Email ?? "A staff member";
                clashes.Add($"{name} is not free for {verdict.BlockedOccurrences} of the {added.Count} {(added.Count == 1 ? "occurrence" : "occurrences")} the change adds. {string.Join(". ", verdict.BlockGroups.Select(group => group.Text))}.");
            }
        }

        Judged(entityId, clashes);
    }

    public TimeZoneInfo TimeZoneOf(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (this.gate)
        {
            return this.zones.TryGetValue(job.Id, out var zone)
                ? zone
                : throw new InvalidOperationException($"No time zone was read for the job {job.Id} before its steps were judged, so its period cannot be judged on its tenant's calendar.");
        }
    }

    public StoredJobReferences StoredReferences(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (this.gate)
        {
            return this.stored.TryGetValue(job.Id, out var stored)
                ? stored
                : throw new InvalidOperationException($"Nothing was read about the job {job.Id} before its steps were judged, so what it names as stored cannot be checked.");
        }
    }

    public IReadOnlyList<string> HolderClashes(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (this.gate)
        {
            if (this.edited.TryGetValue(job.Id, out var edited))
            {
                return edited.Clashes ?? throw new InvalidOperationException($"Whether the staff members of the job {job.Id} stay free cannot be checked: {edited.Unread}");
            }

            return this.clashes.TryGetValue(job.Id, out var clashes)
                ? clashes
                : throw new InvalidOperationException(this.clashesUnread.TryGetValue(job.Id, out var why)
                    ? $"Whether the staff members of the job {job.Id} stay free cannot be checked: {why}"
                    : $"Nothing was read about the job {job.Id} before its steps were judged, so whether its staff members stay free cannot be checked.");
        }
    }

    public void ForgetEdited(Guid entityId)
    {
        lock (this.gate)
        {
            this.edited.Remove(entityId);
        }
    }

    private void Judged(Guid jobId, IReadOnlyList<string> clashes)
    {
        lock (this.gate)
        {
            this.edited[jobId] = new EditedAnswer(clashes, null);
        }
    }

    private void Unread(Guid jobId, string why)
    {
        lock (this.gate)
        {
            this.edited[jobId] = new EditedAnswer(null, why);
        }
    }

    private sealed record EditedAnswer(IReadOnlyList<string>? Clashes, string? Unread);
}
