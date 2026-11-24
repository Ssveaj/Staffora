using Staffora.Core.Entities.Organization;

namespace Staffora.Core.Models;

public sealed record OrganizationUnitNode(Guid Id, Guid? ParentId, string Name, string? Description, OrganizationUnitType UnitType, OrganizationUnitState State)
{
    public bool IsRoot => UnitType == OrganizationUnitType.Company;

    public bool InUse => State == OrganizationUnitState.Active || IsRoot;
}
