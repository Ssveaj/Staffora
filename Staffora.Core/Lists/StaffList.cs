using Staffora.Core.Entities.Resources;

namespace Staffora.Core.Lists;

public static class StaffList
{
    public const string Status = "status";

    public const string Pattern = "pattern";

    public const string PatternCurrent = "current";

    public const string PatternNone = "none";

    public const string PatternEnding = "ending";

    public const string SortName = "name";
    public const string SortEmail = "email";
    public const string SortPattern = "pattern";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortEmail, "Email"), new(SortPattern, "Work pattern")],
        defaultSort: SortName,
        filters:
        [
            new ListFilterDefinition(Status, "Status", ListOption.ForEnum<ResourceState>()),
            new ListFilterDefinition(Pattern, "Work pattern", [new(PatternCurrent, "Has a work pattern"), new(PatternNone, "No work pattern"), new(PatternEnding, "Ending soon")]),
        ]);
}

public static class StaffUnitList
{
    public const string SortUnit = "unit";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortUnit, "Unit")],
        defaultSort: SortUnit);
}
