using Staffora.Core.Entities.Tenancy;

namespace Staffora.Core.Models;

public sealed record TenantRoleRow(Guid Id, string Name, string? Description, TenantRoleState State, bool IsSystemOwner, int PermissionCount, IReadOnlyList<RolePermission> FirstPermissions, bool IsStaffRole, int HolderCount);
