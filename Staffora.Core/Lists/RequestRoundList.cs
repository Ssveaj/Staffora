using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Lists;

public static class RequestRoundList
{
    public const string Status = "status";

    public const string SortSent = "sent";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortSent, "Sent")],
        defaultSort: SortSent,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<RequestRoundState>().Where(option => option.Value != "create").ToList())],
        defaultDescending: true);
}
