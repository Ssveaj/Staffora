using Staffora.Core.Entities.Organization;

namespace Staffora.Core.Lists;

public static class OrganizationUnitList
{
    public const string Type = "type";

    public const string ShowInactive = "inactive";

    public const string ByParent = "parent";

    public const string ByType = "type";

    public const string SortName = "name";
    public const string SortType = "type";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortName, "Name"), new(SortType, "Type")],
        defaultSort: SortName,
        filters: [new ListFilterDefinition(Type, "Type", ListOption.ForEnum<OrganizationUnitType>())],
        toggles: [new ListToggleDefinition(ShowInactive, "Show units not in use", "1 unit not in use is hidden.", "{0} units not in use are hidden.")],
        groups: [new(ByParent, "Parent unit"), new(ByType, "Type")],
        defaultGroup: ByParent);
}
