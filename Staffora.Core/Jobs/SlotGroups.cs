using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Jobs;

public sealed record SlotGroupInput(Guid SlotId, Guid JobId, JobSlotState State, string Name, Guid? ServiceId, IReadOnlyCollection<SlotRequirement> Requirements, DateTime Created)
{
    public static SlotGroupInput Of(JobSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new SlotGroupInput(slot.Id, slot.JobId, slot.State, slot.Name, slot.ServiceId, [.. slot.Qualifications.Select(required => new SlotRequirement(required.QualificationId, required.Level))], slot.Created);
    }
}

public readonly record struct SlotGroupKey(Guid JobId, string Name, Guid? ServiceId, string Requirements)
{
    public static SlotGroupKey Of(SlotGroupInput slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new SlotGroupKey(slot.JobId, NameOf(slot.Name), slot.ServiceId, RequirementsOf(slot.Requirements));
    }

    public static string NameOf(string? name) => (name ?? string.Empty).Trim().ToUpperInvariant();

    public static string RequirementsOf(IEnumerable<SlotRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        return string.Join(",", requirements
            .GroupBy(requirement => requirement.QualificationId)
            .Select(group => (Id: group.Key, Level: group.Min(requirement => requirement.Level)))
            .OrderBy(requirement => requirement.Id)
            .Select(requirement => $"{requirement.Id:N}:{(int)requirement.Level}"));
    }
}

public sealed record SlotGroup(SlotGroupKey Key, IReadOnlyList<Guid> SlotIds)
{
    public Guid Representative => SlotIds[0];

    public int Count => SlotIds.Count;

    public bool Contains(Guid slotId) => SlotIds.Contains(slotId);
}

public static class SlotGroups
{
    public static IReadOnlyList<SlotGroup> Of(IEnumerable<SlotGroupInput> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return [.. slots
            .Where(slot => slot.State == JobSlotState.Active)
            .OrderBy(slot => slot.Created)
            .ThenBy(slot => slot.SlotId)
            .GroupBy(SlotGroupKey.Of)
            .Select(group => new SlotGroup(group.Key, [.. group.Select(slot => slot.SlotId)]))];
    }

    public static SlotGroup? GroupOf(Guid slotId, IEnumerable<SlotGroupInput> slots) =>
        Of(slots).FirstOrDefault(group => group.Contains(slotId));

    public static Guid? FirstOpen(SlotGroup group, Guid? preferred, IReadOnlySet<Guid> heldSlotIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(heldSlotIds);
        if (preferred is { } own && group.Contains(own) && !heldSlotIds.Contains(own))
        {
            return own;
        }

        return group.SlotIds.Where(slot => !heldSlotIds.Contains(slot)).Select(slot => (Guid?)slot).FirstOrDefault();
    }

    public static int Held(int count, int assigned, int accepted) => Math.Min(count, assigned + accepted);

    public static string HeldText(int held, int count) => $"{held} of {count}";
}

public static class JobSlotRules
{
    public const int MaxAddCount = 20;

    public static bool CountInRange(int count) => count is >= 1 and <= MaxAddCount;
}
