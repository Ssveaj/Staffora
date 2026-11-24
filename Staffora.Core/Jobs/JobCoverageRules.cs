using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Jobs;

public enum JobCoverage
{
    [Display(Name = "Uncovered")]
    Uncovered = 0,

    [Display(Name = "Partly covered")]
    PartlyCovered = 1,

    [Display(Name = "Covered")]
    Covered = 2,
}

public readonly record struct SlotCoverageInput(JobSlotState State, bool Held);

public readonly record struct SlotGroupCoverageInput(int Count, int Held);

public static class JobCoverageRules
{
    public static JobCoverage Of(IEnumerable<SlotCoverageInput> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var active = slots.Where(slot => slot.State == JobSlotState.Active).ToList();
        var held = active.Count(slot => slot.Held);
        return held == 0 ? JobCoverage.Uncovered
            : held == active.Count ? JobCoverage.Covered
            : JobCoverage.PartlyCovered;
    }

    public static JobCoverage OfGroups(IEnumerable<SlotGroupCoverageInput> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var all = groups.ToList();
        return OfGroup(all.Sum(group => group.Count), all.Sum(group => Math.Min(group.Count, group.Held)));
    }

    public static JobCoverage OfGroup(int count, int held) =>
        held <= 0 || count <= 0 ? JobCoverage.Uncovered
        : held >= count ? JobCoverage.Covered
        : JobCoverage.PartlyCovered;

    public static string Label(JobCoverage coverage) => coverage switch
    {
        JobCoverage.Covered => "Covered",
        JobCoverage.PartlyCovered => "Partly covered",
        _ => "Uncovered",
    };
}
