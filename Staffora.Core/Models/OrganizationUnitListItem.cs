using Staffora.Core.Entities.Organization;

namespace Staffora.Core.Models;

public sealed record OrganizationUnitListItem(
    Guid Id,
    Guid? ParentId,
    string Name,
    string? Description,
    OrganizationUnitType UnitType,
    OrganizationUnitState State,
    string ParentPath,
    int UnitsBelow,
    int? RoleHoldings)
{
    public bool IsRoot => UnitType == OrganizationUnitType.Company;

    public string TypeName { get; init; } = string.Empty;

    public bool InUse => State == OrganizationUnitState.Active || IsRoot;

    public string Path => ParentPath.Length == 0 ? Name : ParentPath + OrganizationUnitOutline.PathSeparator + Name;
}
