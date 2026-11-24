using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;

namespace Staffora.Services.Jobs;

internal sealed class OpenAgainFacts
{
    public static readonly OpenAgainFacts None = new(new Dictionary<Guid, List<DateTime>>(), new Dictionary<Guid, List<DateTime>>());

    private readonly IReadOnlyDictionary<Guid, List<DateTime>> takenOff;
    private readonly IReadOnlyDictionary<Guid, List<DateTime>> held;

    public OpenAgainFacts(IReadOnlyDictionary<Guid, List<DateTime>> takenOff, IReadOnlyDictionary<Guid, List<DateTime>> held)
    {
        this.takenOff = takenOff;
        this.held = held;
    }

    public bool TakenOffIn(IEnumerable<Guid> groupSlotIds) => groupSlotIds.Any(this.takenOff.ContainsKey);

    public int CountFor(IReadOnlyCollection<Guid> groupSlotIds, int open)
    {
        ArgumentNullException.ThrowIfNull(groupSlotIds);
        return !TakenOffIn(groupSlotIds) ? 0
            : AbsenceImpactRules.OpenAgain(
                groupSlotIds.SelectMany(slot => this.takenOff.GetValueOrDefault(slot) ?? []),
                groupSlotIds.SelectMany(slot => this.held.GetValueOrDefault(slot) ?? []),
                open);
    }
}

internal static class OpenAgainReader
{
    public static readonly string[] TakeOffKeys = [.. SlotAssignmentWorkflowAttribute.TakeOffSteps];

    public static async Task<OpenAgainFacts> ReadAsync(ApplicationDbContext context, Guid tenantId, IReadOnlyCollection<IReadOnlyCollection<Guid>> groups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(groups);
        var slotIds = groups.SelectMany(group => group).Distinct().ToList();
        if (slotIds.Count == 0)
        {
            return OpenAgainFacts.None;
        }

        var takenOff = (await context.StateHistoryEntries.AsNoTracking()
                .Where(entry => entry.TenantId == tenantId && entry.EntityType == nameof(SlotAssignment) && TakeOffKeys.Contains(entry.TransitionKey))
                .Join(
                    context.SlotAssignments.Where(assignment => assignment.TenantId == tenantId && assignment.State == SlotAssignmentState.Canceled && slotIds.Contains(assignment.JobSlotId)),
                    entry => entry.EntityId,
                    assignment => assignment.Id,
                    (entry, assignment) => new { assignment.JobSlotId, entry.Created })
                .ToListAsync(cancellationToken))
            .GroupBy(found => found.JobSlotId)
            .ToDictionary(slot => slot.Key, slot => slot.Select(found => found.Created).ToList());
        if (takenOff.Count == 0)
        {
            return OpenAgainFacts.None;
        }

        var touched = groups.Where(group => group.Any(takenOff.ContainsKey)).SelectMany(group => group).Distinct().ToList();
        var assigned = (int)SlotAssignmentState.Assigned;
        var held = (await context.StateHistoryEntries.AsNoTracking()
                .Where(entry => entry.TenantId == tenantId && entry.EntityType == nameof(SlotAssignment) && entry.ToState == assigned && entry.FromState != assigned)
                .Join(
                    context.SlotAssignments.Where(assignment => assignment.TenantId == tenantId && assignment.State == SlotAssignmentState.Assigned && touched.Contains(assignment.JobSlotId)),
                    entry => entry.EntityId,
                    assignment => assignment.Id,
                    (entry, assignment) => new { assignment.Id, assignment.JobSlotId, entry.Created })
                .ToListAsync(cancellationToken))
            .GroupBy(found => (found.Id, found.JobSlotId))
            .GroupBy(assignment => assignment.Key.JobSlotId)
            .ToDictionary(slot => slot.Key, slot => slot.Select(assignment => assignment.Max(found => found.Created)).ToList());
        return new OpenAgainFacts(takenOff, held);
    }

    public static async Task<IReadOnlyDictionary<Guid, int>> OfJobsAsync(ApplicationDbContext context, Guid tenantId, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var groups = SlotGroups.Of(await SlotGroupReader.ReadAsync(context, jobIds, cancellationToken));
        var facts = await ReadAsync(context, tenantId, [.. groups.Select(group => (IReadOnlyCollection<Guid>)group.SlotIds)], cancellationToken);
        var answer = new Dictionary<Guid, int>();
        var touched = groups.Where(group => facts.TakenOffIn(group.SlotIds)).ToList();
        if (touched.Count == 0)
        {
            return answer;
        }

        var slotIds = touched.SelectMany(group => group.SlotIds).ToList();
        var assigned = (await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId && assignment.State == SlotAssignmentState.Assigned && slotIds.Contains(assignment.JobSlotId))
                .Select(assignment => assignment.JobSlotId)
                .ToListAsync(cancellationToken))
            .GroupBy(slot => slot)
            .ToDictionary(slot => slot.Key, slot => slot.Count());
        foreach (var group in touched)
        {
            var open = Math.Max(0, group.Count - group.SlotIds.Sum(slot => assigned.GetValueOrDefault(slot)));
            var count = facts.CountFor(group.SlotIds, open);
            foreach (var slot in group.SlotIds)
            {
                answer[slot] = count;
            }
        }

        return answer;
    }
}
