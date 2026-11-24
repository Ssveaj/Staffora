using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Lists;

public static class JobList
{
    public const string Status = "status";

    public const string SortPeriodStart = "period";
    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortPeriodStart, "Period"), new(SortName, "Name")],
        defaultSort: SortPeriodStart,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<JobState>().Where(option => option.Value is not ("create" or "deleted")).ToList())],
        defaultDescending: true);
}

public static class SlotAssignmentList
{
    public const string Status = "status";

    public const string SortStaff = "staff";
    public const string SortCreated = "created";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortStaff, "Staff"), new(SortCreated, "Asked")],
        defaultSort: SortStaff,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<SlotAssignmentState>().Where(option => option.Value != "create").ToList())]);
}

public static class JobSlotList
{
    public const string Status = "status";

    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<JobSlotState>().Where(option => option.Value != "deleted").ToList())]);
}

public static class StaffingCandidateList
{
    public const string Availability = "availability";

    public const string Free = "free";

    public const string Blocked = "blocked";

    public const string SortRank = "rank";
    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortRank, "Rank"), new(SortName, "Name")],
        defaultSort: SortRank,
        filters: [new ListFilterDefinition(Availability, "Availability", [new(Free, "Free"), new(Blocked, "Blocked")])]);
}
