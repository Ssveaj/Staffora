using Staffora.Core.Entities.Catalogue;

namespace Staffora.Core.Lists;

public static class ServiceList
{
    public const string Status = "status";

    public const string Portal = "portal";

    public const string Offered = "offered";

    public const string NotOffered = "not-offered";

    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name")],
        defaultSort: SortName,
        filters:
        [
            new ListFilterDefinition(Status, "Status", ListOption.ForEnum<ServiceState>().Where(option => option.Value != nameof(ServiceState.Create).ToLowerInvariant()).ToList()),
            new ListFilterDefinition(Portal, "Portal", [new ListOption(Offered, "Offered"), new ListOption(NotOffered, "Not offered")]),
        ]);
}
