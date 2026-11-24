using Staffora.Core.Entities.Scheduling;

namespace Staffora.Core.Lists;

public static class StaffScheduleList
{
    public const string Status = "status";

    public const string SortFrom = "from";
    public const string SortPattern = "pattern";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortFrom, "From"), new(SortPattern, "Pattern")],
        defaultSort: SortFrom,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<ResourcePatternAssignmentState>().Where(option => option.Value != nameof(ResourcePatternAssignmentState.Deleted).ToLowerInvariant()).ToList())],
        defaultDescending: true);
}
