namespace Staffora.Core.Lists;

public static class OrganizationUnitChildList
{
    public const string SortName = "name";
    public const string SortType = "type";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortType, "Type")],
        defaultSort: SortName);
}
