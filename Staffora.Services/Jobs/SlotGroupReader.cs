using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Data;

namespace Staffora.Services.Jobs;

internal static class SlotGroupReader
{
    public static async Task<IReadOnlyList<SlotGroupInput>> ReadAsync(ApplicationDbContext context, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jobIds);
        if (jobIds.Count == 0)
        {
            return [];
        }

        var slots = await context.JobSlots.AsNoTracking()
            .Where(slot => jobIds.Contains(slot.JobId) && slot.State != JobSlotState.Deleted)
            .OrderBy(slot => slot.Id)
            .Select(slot => new { slot.Id, slot.JobId, slot.State, slot.Name, slot.Created, slot.ServiceId, Requirements = slot.Qualifications.Select(link => new { link.QualificationId, link.Level }).ToList() })
            .AsSingleQuery()
            .ToListAsync(cancellationToken);
        return [.. slots.Select(slot => new SlotGroupInput(slot.Id, slot.JobId, slot.State, slot.Name, slot.ServiceId, [.. slot.Requirements.Select(link => new SlotRequirement(link.QualificationId, link.Level))], slot.Created))];
    }

    public static async Task<(SlotGroup Group, IReadOnlyList<SlotGroupInput> Slots)?> GroupOfAsync(ApplicationDbContext context, Guid slotId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var jobId = await context.JobSlots.AsNoTracking().Where(slot => slot.Id == slotId).Select(slot => (Guid?)slot.JobId).FirstOrDefaultAsync(cancellationToken);
        if (jobId is not { } job)
        {
            return null;
        }

        var slots = await ReadAsync(context, [job], cancellationToken);
        var group = SlotGroups.GroupOf(slotId, slots) ?? (slots.FirstOrDefault(slot => slot.SlotId == slotId) is { } own ? new SlotGroup(SlotGroupKey.Of(own), [slotId]) : null);
        return group is null ? null : (group, slots);
    }

    public static IReadOnlyList<IReadOnlyList<SlotGroupInput>> Entries(IReadOnlyList<SlotGroupInput> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var byId = slots.ToDictionary(slot => slot.SlotId);
        var grouped = SlotGroups.Of(slots).Select(group => (IReadOnlyList<SlotGroupInput>)[.. group.SlotIds.Select(id => byId[id])]).ToList();
        var single = slots.Where(slot => slot.State != JobSlotState.Active).Select(slot => (IReadOnlyList<SlotGroupInput>)[slot]);
        return [.. grouped.Concat(single).OrderBy(entry => entry[0].Created).ThenBy(entry => entry[0].SlotId)];
    }
}
