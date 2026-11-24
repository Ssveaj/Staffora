namespace Staffora.Core.Lists;

public static class UnitRoleHoldingList
{
    public const string SortPerson = "person";
    public const string SortRole = "role";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortPerson, "Person"), new(SortRole, "Role")],
        defaultSort: SortPerson);
}

public static class RoleHolderList
{
    public const string SortPerson = "person";
    public const string SortUnit = "unit";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortPerson, "Person"), new(SortUnit, "Unit")],
        defaultSort: SortPerson);
}
