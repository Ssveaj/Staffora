using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;

namespace Staffora.Services.Services;

public sealed record TenantRoleEditorOptions(bool FieldsLocked, string? LockedReason, IReadOnlySet<RolePermission> Grantable, bool CanManageHolders, RoleHoldingChoices Choices)
{
    public IReadOnlySet<RolePermission> Offered { get; init; } = RolePermissions.Grantable.ToHashSet();
}
