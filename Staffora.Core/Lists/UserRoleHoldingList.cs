namespace Staffora.Core.Lists;

public static class UserRoleHoldingList
{
    public const string SortRole = "role";
    public const string SortUnit = "unit";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortRole, "Role"), new(SortUnit, "Unit")],
        defaultSort: SortRole);
}
