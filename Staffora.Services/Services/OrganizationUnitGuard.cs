using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Services.Services;

public sealed class OrganizationUnitGuard : IOrganizationUnitGuard, IStepPreparation
{
    private readonly Dictionary<Guid, bool> beyond = [];
    private readonly Lock gate = new();
    private readonly PermissionService permissions;

    public OrganizationUnitGuard(PermissionService permissions)
    {
        this.permissions = permissions;
    }

    public Type EntityType => typeof(OrganizationUnit);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenantId = await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.Id == entityId)
            .Select(unit => (Guid?)unit.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenantId is not { } tenant)
        {
            Forget([entityId]);
            return;
        }

        var links = await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.TenantId == tenant)
            .Select(unit => new { unit.Id, unit.ParentId })
            .ToListAsync(cancellationToken);
        var below = links.Where(link => link.ParentId is not null).ToLookup(link => link.ParentId!.Value, link => link.Id);
        var subtree = new HashSet<Guid> { entityId };
        var pending = new Queue<Guid>([entityId]);
        while (pending.TryDequeue(out var id))
        {
            foreach (var child in below[id].Where(subtree.Add))
            {
                pending.Enqueue(child);
            }
        }

        var granted = await context.OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => holding.TenantId == tenant && subtree.Contains(holding.OrganizationUnitId))
            .Join(context.TenantRolePermissions, holding => holding.TenantRoleId, permission => permission.TenantRoleId, (holding, permission) => new { holding.OrganizationUnitId, permission.Permission })
            .Distinct()
            .ToListAsync(cancellationToken);
        var missing = await this.permissions.MissingAsync(granted.Select(row => row.Permission).Distinct(), cancellationToken);
        var beyond = granted.Where(row => missing.Contains(row.Permission)).Select(row => row.OrganizationUnitId).ToHashSet();

        lock (this.gate)
        {
            foreach (var unit in subtree)
            {
                this.beyond[unit] = beyond.Contains(unit);
            }
        }
    }

    public bool HoldingsBeyondTheSession(OrganizationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        lock (this.gate)
        {
            return this.beyond.TryGetValue(unit.Id, out var beyond)
                ? beyond
                : throw new InvalidOperationException($"Nothing was read about unit {unit.Id} before its steps were judged, so the grant ceiling cannot be checked.");
        }
    }

    private void Forget(IEnumerable<Guid> units)
    {
        lock (this.gate)
        {
            foreach (var unit in units)
            {
                this.beyond.Remove(unit);
            }
        }
    }
}
