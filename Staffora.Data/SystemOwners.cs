using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data;

public static class SystemOwners
{
    public static IQueryable<OrganizationUnitUserRole> Holdings(ApplicationDbContext context, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.OrganizationUnitUserRoles.Where(holding => holding.TenantId == tenantId
            && holding.TenantRole.IsSystemOwner
            && holding.TenantRole.State == TenantRoleState.Active);
    }

    public static IQueryable<Guid> ActiveOwnerIds(ApplicationDbContext context, Guid tenantId) =>
        Holdings(context, tenantId)
            .Where(holding => holding.User.State == UserState.Active && !holding.User.IsSystemAccount)
            .Select(holding => holding.UserId)
            .Distinct();

    public static Task<bool> IsActiveOwnerAsync(ApplicationDbContext context, Guid tenantId, Guid userId, CancellationToken cancellationToken = default) =>
        ActiveOwnerIds(context, tenantId).AnyAsync(id => id == userId, cancellationToken);

    public static Task<bool> HoldsTheRoleAsync(ApplicationDbContext context, Guid tenantId, Guid userId, CancellationToken cancellationToken = default) =>
        Holdings(context, tenantId).AnyAsync(holding => holding.UserId == userId, cancellationToken);

    public static Task<Guid?> RoleIdAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TenantRoles.AsNoTracking()
            .Where(role => role.TenantId == tenantId && role.IsSystemOwner)
            .Select(role => (Guid?)role.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
