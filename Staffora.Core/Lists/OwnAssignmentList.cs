using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Lists;

public static class OwnAssignmentList
{
    public const string Status = "status";

    public const string ShowClosed = "closed";

    public const string SortPeriod = "period";
    public const string SortJob = "job";
    public const string SortAsked = "asked";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortPeriod, "Period"), new(SortJob, "Job"), new(SortAsked, "Asked")],
        defaultSort: SortPeriod,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<SlotAssignmentState>().Where(option => option.Value is not ("create" or "expired")).ToList())],
        toggles: [new ListToggleDefinition(ShowClosed, "Show closed", "1 closed request or assignment is hidden.", "{0} closed requests or assignments are hidden.")]);
}
