using Staffora.Core.Entities.Organization;

namespace Staffora.Core.Models;

public sealed record UserRoleHoldingRow(Guid Id, string Role, string Unit, OrganizationUnitState UnitState, bool UnitIsRoot, Guid TenantId, Guid RoleId)
{
    public string? Person { get; init; }

    public Guid UserId { get; init; }

    public bool UnitInUse => UnitIsRoot || UnitState == OrganizationUnitState.Active;

    public string UnitLabel => Unit + OrganizationUnitOutline.MarkOf(UnitState, UnitIsRoot);
}
