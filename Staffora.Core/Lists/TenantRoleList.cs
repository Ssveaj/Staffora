namespace Staffora.Core.Lists;

public static class TenantRoleList
{
    public const string SortName = "name";
    public const string SortPermissions = "permissions";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortPermissions, "Permissions")],
        defaultSort: SortName);
}
