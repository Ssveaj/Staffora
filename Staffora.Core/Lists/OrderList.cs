using Staffora.Core.Entities.Portal;

namespace Staffora.Core.Lists;

public static class OrderList
{
    public const string Status = "status";

    public const string Everyone = "everyone";

    public const string SortWritten = "written";
    public const string SortJob = "job";
    public const string SortPeriod = "period";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortWritten, "Written"), new(SortJob, "Job"), new(SortPeriod, "Period")],
        defaultSort: SortWritten,
        defaultDescending: true,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<OrderState>().Where(option => option.Value != "create").ToList())],
        toggles: [new ListToggleDefinition(Everyone, "Everyone's orders", "1 order of someone else is hidden.", "{0} orders of others are hidden.")]);
}
