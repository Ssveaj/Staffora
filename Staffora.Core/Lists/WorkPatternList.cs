using Staffora.Core.Entities.Scheduling;

namespace Staffora.Core.Lists;

public static class WorkPatternList
{
    public const string Status = "status";

    public const string SortName = "name";
    public const string SortCycle = "cycle";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortCycle, "Cycle length")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<WorkPatternState>().Where(option => option.Value != nameof(WorkPatternState.Deleted).ToLowerInvariant()).ToList())]);
}

public static class WorkPatternStaffList
{
    public const string When = "when";

    public const string WhenCurrent = "current";
    public const string WhenUpcoming = "upcoming";
    public const string WhenEnded = "ended";

    public const string Status = "status";

    public const string SortFrom = "from";
    public const string SortStaff = "staff";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortFrom, "From"), new(SortStaff, "Staff member")],
        defaultSort: SortFrom,
        filters:
        [
            new ListFilterDefinition(When, "Dates", [new(WhenCurrent, "Current"), new(WhenUpcoming, "Upcoming"), new(WhenEnded, "Ended")]),
            new ListFilterDefinition(Status, "Status", ListOption.ForEnum<ResourcePatternAssignmentState>().Where(option => option.Value != nameof(ResourcePatternAssignmentState.Deleted).ToLowerInvariant()).ToList()),
        ],
        defaultDescending: true);
}
