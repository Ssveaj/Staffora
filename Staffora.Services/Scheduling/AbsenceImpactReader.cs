using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Data;
using Staffora.Services.Jobs;

namespace Staffora.Services.Scheduling;

public sealed record AbsenceImpactRow(Guid AssignmentId, Guid JobId, Guid SlotId, string Job, string Slot, string Customer, SlotAssignmentState State, IReadOnlyList<OccurrenceInstant> Occurrences)
{
    public const int OccurrencesShown = 3;

    public bool TakenOff { get; init; }

    public bool OpenAgain { get; init; }

    public bool Settled => TakenOff;

    public DateTime FirstUtc => Occurrences[0].StartUtc;

    public string StateText => TakenOff ? "Taken off" : typeof(SlotAssignmentState).GetField(State.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? State.ToString();

    public string OccurrencesText => Occurrences.Count <= OccurrencesShown
        ? string.Join(", ", Occurrences.Select(occurrence => occurrence.Text))
        : $"{string.Join(", ", Occurrences.Take(OccurrencesShown).Select(occurrence => occurrence.Text))} and {Occurrences.Count - OccurrencesShown} more";
}

public sealed record AbsenceImpact(Guid AbsenceId, string Staff, AbsenceKind Kind, ResourceAbsenceState State, string Period, IReadOnlyList<AbsenceImpactRow> Rows)
{
    public bool Blocks => AbsenceRules.Blocks(Kind, State);

    public bool Scheduled => AbsenceRules.ScheduledStates.Contains(State);

    public int Unsettled => Rows.Count(row => !row.Settled);
}

internal static class AbsenceImpactReader
{
    private static readonly SlotAssignmentState[] LiveStates = [SlotAssignmentState.Requested, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned];

    private static readonly SlotAssignmentState[] WithTakenOffStates = [.. LiveStates, SlotAssignmentState.Canceled];

    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AbsenceImpactRow>>> ReadAsync(ApplicationDbContext context, Guid tenantId, IReadOnlyList<ScheduledAbsence> absences, DateTime nowUtc, TimeZoneInfo zone, bool withTakenOff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(absences);
        var answer = absences.ToDictionary(absence => absence.AbsenceId, _ => (IReadOnlyList<AbsenceImpactRow>)[]);
        if (absences.Count == 0)
        {
            return answer;
        }

        var staff = absences.Select(absence => absence.ResourceId).Distinct().ToList();
        var earliest = absences.Min(absence => absence.FirstDay);
        var first = earliest > DateOnly.MinValue.AddDays(1) ? earliest.AddDays(-1) : earliest;
        var last = absences.Max(absence => absence.LastDay);
        var states = withTakenOff ? WithTakenOffStates : LiveStates;
        var assignments = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId
                && staff.Contains(assignment.ResourceId)
                && states.Contains(assignment.State)
                && assignment.Slot.State == JobSlotState.Active
                && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress)
                && assignment.Slot.Job.PeriodStart <= last
                && assignment.Slot.Job.PeriodEnd >= first)
            .Select(assignment => new
            {
                assignment.Id,
                assignment.ResourceId,
                assignment.State,
                assignment.JobSlotId,
                assignment.Slot.JobId,
                Slot = assignment.Slot.Name,
                Job = assignment.Slot.Job.Name,
                Customer = assignment.Slot.Job.Customer.Name,
                assignment.Slot.Job.Schedule,
                assignment.Slot.Job.PeriodStart,
                assignment.Slot.Job.PeriodEnd,
                assignment.Slot.Job.StartTime,
                assignment.Slot.Job.EndTime,
                Weekly = assignment.Slot.Job.TimeSlots.Select(time => new { time.Weekday, time.Start, time.End }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var canceled = assignments.Where(assignment => assignment.State == SlotAssignmentState.Canceled).Select(assignment => assignment.Id).ToList();
        var takenOff = canceled.Count == 0
            ? []
            : (await context.StateHistoryEntries.AsNoTracking()
                    .Where(entry => entry.TenantId == tenantId && entry.EntityType == nameof(SlotAssignment) && OpenAgainReader.TakeOffKeys.Contains(entry.TransitionKey) && canceled.Contains(entry.EntityId))
                    .Select(entry => entry.EntityId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        var shown = assignments.Where(assignment => assignment.State != SlotAssignmentState.Canceled || takenOff.Contains(assignment.Id)).ToList();
        var openAgain = takenOff.Count == 0
            ? new Dictionary<Guid, int>()
            : await OpenAgainReader.OfJobsAsync(context, tenantId, [.. shown.Where(assignment => takenOff.Contains(assignment.Id)).Select(assignment => assignment.JobId).Distinct()], cancellationToken);

        foreach (var absence in absences)
        {
            answer[absence.AbsenceId] = [.. shown
                .Where(assignment => assignment.ResourceId == absence.ResourceId)
                .Select(assignment => new AbsenceImpactRow(
                    assignment.Id,
                    assignment.JobId,
                    assignment.JobSlotId,
                    assignment.Job,
                    assignment.Slot,
                    assignment.Customer,
                    assignment.State,
                    AbsenceImpactRules.AffectedOccurrences(
                        absence,
                        new JobTimes(assignment.Schedule, assignment.PeriodStart, assignment.PeriodEnd, assignment.StartTime, assignment.EndTime, [.. assignment.Weekly.Select(time => new OccurrenceTimes(time.Weekday, time.Start, time.End))]),
                        nowUtc,
                        zone))
                {
                    TakenOff = takenOff.Contains(assignment.Id),
                    OpenAgain = takenOff.Contains(assignment.Id) && openAgain.GetValueOrDefault(assignment.JobSlotId) > 0,
                })
                .Where(row => row.Occurrences.Count > 0)
                .OrderBy(row => row.Settled)
                .ThenBy(row => row.FirstUtc)
                .ThenBy(row => row.Job, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.AssignmentId)];
        }

        return answer;
    }
}
