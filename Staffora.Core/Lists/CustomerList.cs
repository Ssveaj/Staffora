using Staffora.Core.Entities.Customers;

namespace Staffora.Core.Lists;

public static class CustomerList
{
    public const string Status = "status";

    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<CustomerState>().Where(option => option.Value != nameof(CustomerState.Deleted).ToLowerInvariant()).ToList())]);
}

public static class CustomerContactList
{
    public const string Status = "status";

    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Status, "Status", ListOption.ForEnum<CustomerContactState>().Where(option => option.Value != nameof(CustomerContactState.Deleted).ToLowerInvariant()).ToList())]);
}
