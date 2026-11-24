using Staffora.Core.Entities.Scheduling;

namespace Staffora.Core.Lists;

public static class AbsenceList
{
    public const string Status = "status";

    public const string Kind = "kind";

    public const string SortFirstDay = "from";
    public const string SortStaff = "staff";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortFirstDay, "First day"), new(SortStaff, "Staff")],
        defaultSort: SortFirstDay,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<ResourceAbsenceState>()), new ListFilterDefinition(Kind, "Kind", ListOption.ForEnum<AbsenceKind>())],
        defaultDescending: true);
}

public static class StaffAbsenceList
{
    public const string Status = "status";

    public const string Kind = "kind";

    public const string SortFirstDay = "from";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortFirstDay, "First day")],
        defaultSort: SortFirstDay,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<ResourceAbsenceState>()), new ListFilterDefinition(Kind, "Kind", ListOption.ForEnum<AbsenceKind>())],
        defaultDescending: true);
}
