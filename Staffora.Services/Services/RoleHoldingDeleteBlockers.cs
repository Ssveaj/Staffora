using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Services;

public sealed class RoleHoldingsBlockUnitDelete : IDeleteBlocker
{
    public Type EntityType => typeof(OrganizationUnit);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(OrganizationUnitUserRole)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        return await context.OrganizationUnitUserRoles.AsNoTracking().AnyAsync(holding => holding.OrganizationUnitId == entity.Id, cancellationToken) ? Reason : null;
    }

    public const string Reason = "Roles are still held on this unit: remove the role holdings first, or archive the unit instead.";
}

public sealed class RoleHoldersBlockRoleDelete : IDeleteBlocker
{
    public Type EntityType => typeof(TenantRole);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(OrganizationUnitUserRole)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var holdings = await context.OrganizationUnitUserRoles.AsNoTracking().CountAsync(holding => holding.TenantRoleId == entity.Id, cancellationToken);
        return holdings == 0 ? null : Reason(holdings);
    }

    public static string Reason(int holdings) => holdings == 1
        ? "Someone holds this role: remove it from them first."
        : $"This role is held {holdings} times: remove it from its holders first.";
}
