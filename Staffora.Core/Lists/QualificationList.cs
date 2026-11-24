using Staffora.Core.Entities.Resources;

namespace Staffora.Core.Lists;

public static class QualificationList
{
    public const string Status = "status";

    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<QualificationState>().Where(option => option.Value != nameof(QualificationState.Create).ToLowerInvariant()).ToList())]);
}

public static class StaffQualificationList
{
    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Qualification")],
        defaultSort: SortName);
}

public static class QualificationHolderList
{
    public const string Status = "status";

    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<ResourceState>())]);
}
