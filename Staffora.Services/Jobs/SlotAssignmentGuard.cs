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

public sealed class SlotAssignmentGuard : ISlotAssignmentGuard, IStepPreparation
{
    private readonly Dictionary<(Guid Slot, Guid Staff), StaffingVerdict> verdicts = [];
    private readonly Dictionary<(Guid Slot, Guid Staff), ScheduledAbsence?> absences = [];
    private readonly Dictionary<(Guid Slot, Guid Staff), string> unread = [];
    private readonly Lock gate = new();
    private readonly TenantClock tenantClock;
    private readonly TimeProvider clock;

    public SlotAssignmentGuard(TenantClock tenantClock, TimeProvider clock)
    {
        this.tenantClock = tenantClock;
        this.clock = clock;
    }

    public Type EntityType => typeof(SlotAssignment);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stored = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.Id == entityId)
            .Select(assignment => new { assignment.TenantId, assignment.JobSlotId, assignment.ResourceId, assignment.State })
            .FirstOrDefaultAsync(cancellationToken);
        if (stored is { State: SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion or SlotAssignmentState.Accepted or SlotAssignmentState.Assigned })
        {
            await JudgeAsync(context, stored.TenantId, stored.JobSlotId, stored.ResourceId, entityId, cancellationToken);
        }
    }

    public async Task PrepareNewAsync(ApplicationDbContext context, object entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (entity is SlotAssignment assignment)
        {
            await JudgeAsync(context, assignment.TenantId, assignment.JobSlotId, assignment.ResourceId, ownAssignmentId: null, cancellationToken);
        }
    }

    public StaffingVerdict VerdictOf(SlotAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        lock (this.gate)
        {
            return this.verdicts.TryGetValue((assignment.JobSlotId, assignment.ResourceId), out var verdict)
                ? verdict
                : throw new InvalidOperationException(this.unread.TryGetValue((assignment.JobSlotId, assignment.ResourceId), out var why)
                    ? $"Whether the staff member of the assignment {assignment.Id} is free cannot be checked: {why}"
                    : $"Nothing was read about the staff member of the assignment {assignment.Id} before its steps were judged, so whether they are free cannot be checked.");
        }
    }

    public ScheduledAbsence? BlockingAbsenceOf(SlotAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        _ = VerdictOf(assignment);
        lock (this.gate)
        {
            return this.absences.GetValueOrDefault((assignment.JobSlotId, assignment.ResourceId));
        }
    }

    private async Task JudgeAsync(ApplicationDbContext context, Guid tenantId, Guid slotId, Guid resourceId, Guid? ownAssignmentId, CancellationToken cancellationToken)
    {
        var job = await context.JobSlots.AsNoTracking()
            .Where(slot => slot.Id == slotId && (tenantId == Guid.Empty || slot.TenantId == tenantId))
            .Select(slot => new
            {
                slot.TenantId,
                slot.Job.Schedule,
                slot.Job.PeriodStart,
                slot.Job.PeriodEnd,
                slot.Job.StartTime,
                slot.Job.EndTime,
                Occurrences = slot.Job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            Forget(slotId, resourceId, "its slot was not found.");
            return;
        }

        tenantId = job.TenantId;
        var zone = await this.tenantClock.GetTimeZoneOfAsync(tenantId, cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
        {
            Forget(slotId, resourceId, $"the time zone of the tenant {tenantId} could not be read: {zone.Error}");
            return;
        }

        var holidays = await this.tenantClock.GetHolidaysOfAsync(tenantId, cancellationToken);
        if (!holidays.IsSuccess || holidays.Value is not { } tenantHolidays)
        {
            Forget(slotId, resourceId, $"the public holidays of the tenant {tenantId} could not be read: {holidays.Error}");
            return;
        }

        var now = this.clock.GetUtcNow().UtcDateTime;
        var occurrences = StaffingRules.RemainingOccurrences(new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, [.. job.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))]), now, timeZone);
        var verdict = StaffingVerdict.None;
        ScheduledAbsence? absence = null;
        if (occurrences.Count > 0 && StaffingRules.RemainingRange(job.PeriodStart, job.PeriodEnd, now, timeZone) is { } range)
        {
            var answer = await StaffAvailabilityService.ComputeAsync(context, tenantId, [resourceId], range.From, range.To, timeZone, tenantHolidays, cancellationToken);
            verdict = answer.StaffMembers.TryGetValue(resourceId, out var staff)
                ? StaffingRules.Judge(staff, occurrences, ownAssignmentId)
                : new StaffingVerdict(occurrences.Count, [], []);
            absence = await BlockingAbsenceAsync(context, tenantId, resourceId, range, verdict, cancellationToken);
        }

        lock (this.gate)
        {
            this.verdicts[(slotId, resourceId)] = verdict;
            this.absences[(slotId, resourceId)] = absence;
            this.unread.Remove((slotId, resourceId));
        }
    }

    private static async Task<ScheduledAbsence?> BlockingAbsenceAsync(ApplicationDbContext context, Guid tenantId, Guid resourceId, (DateOnly From, DateOnly To) range, StaffingVerdict verdict, CancellationToken cancellationToken)
    {
        var sources = verdict.Blocks.Where(finding => finding.SourceId is not null).Select(finding => finding.SourceId!.Value).Distinct().ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        var absences = await context.ResourceAbsences.AsNoTracking()
            .Where(absence => absence.TenantId == tenantId
                && absence.ResourceId == resourceId
                && sources.Contains(absence.Id)
                && absence.FirstDay <= range.To
                && absence.LastDay >= range.From)
            .Select(absence => new ScheduledAbsence(absence.Id, absence.ResourceId, absence.Kind, absence.State, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime))
            .ToListAsync(cancellationToken);
        return verdict.Blocks
            .OrderBy(finding => finding.Occurrence.StartUtc)
            .Select(finding => absences.FirstOrDefault(absence => absence.AbsenceId == finding.SourceId && AbsenceRules.Blocks(absence.Kind, absence.State)))
            .FirstOrDefault(absence => absence is not null);
    }

    private void Forget(Guid slotId, Guid resourceId, string why)
    {
        lock (this.gate)
        {
            this.verdicts.Remove((slotId, resourceId));
            this.absences.Remove((slotId, resourceId));
            this.unread[(slotId, resourceId)] = why;
        }
    }
}
