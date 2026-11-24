namespace Staffora.Core.Lists;

public static class UserList
{
    public const string Status = "status";

    public const string Active = "active";
    public const string Inactive = "inactive";

    public const string ByStatus = "status";

    public const string SortName = "name";
    public const string SortEmail = "email";
    public const string SortUserName = "username";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortEmail, "Email"), new(SortUserName, "User name")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Status, "Status", [new(Active, "Active"), new(Inactive, "Inactive")])],
        groups: [new(ByStatus, "Status")]);
}
