namespace Staffora.Blazor.Helper.UrlState;

public static class UrlStateKeys
{
    public const string DialogStack = "dlg";

    public const string Search = Staffora.Core.Lists.ListDefinition.SearchKey;

    public const string Status = Staffora.Core.Lists.UserList.Status;

    public const string Level = "level";

    public const string Days = "days";

    public const string Group = Staffora.Core.Lists.ListDefinition.GroupKey;

    public const string Sort = Staffora.Core.Lists.ListDefinition.SortKey;

    public const string Page = Staffora.Core.Lists.ListDefinition.PageKey;

    public const string Month = "month";

    public const string Day = "day";

    public const string Dock = "dock";

    public const string View = "view";

    public const string Type = Staffora.Core.Lists.OrganizationUnitList.Type;

    public const string Inactive = Staffora.Core.Lists.OrganizationUnitList.ShowInactive;

    public const string Entity = Staffora.Core.Lists.CustomFieldList.Entity;

    public const string Scope = Staffora.Core.Lists.CustomFieldList.Scope;

    public const int MaxValueLength = 512;

    public static bool IsValidName(string? name) =>
        name is { Length: > 0 } && name.Split('.') is { Length: 1 or 2 } parts && parts.All(IsValidSegment);

    private static bool IsValidSegment(string segment) =>
        segment is { Length: > 0 and <= 16 }
        && segment[0] is >= 'a' and <= 'z'
        && segment.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9'));
}
