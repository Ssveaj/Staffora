using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;

namespace Staffora.Data;

public static class CompanyRoot
{
    public static async Task<(Guid Id, bool Created)> EnsureAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var lease = context.BeginSystemScope("ensure a tenant's Company root");

        var ownTransaction = context.Database.CurrentTransaction is null ? await context.Database.BeginTransactionAsync(ApplicationDbContext.OrganizationStructureIsolationLevel, cancellationToken) : null;
        try
        {
            await context.LockOrganizationStructureAsync(tenantId, cancellationToken);
            var (rootId, created) = await AddUnlessFoundAsync(context, tenantId, name, acceptConcurrentRoot: ownTransaction is not null, cancellationToken);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }

            return (rootId, created);
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }
    }

    private static async Task<(Guid Id, bool Created)> AddUnlessFoundAsync(ApplicationDbContext context, Guid tenantId, string? name, bool acceptConcurrentRoot, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(context, tenantId, cancellationToken);
        if (existing is { } id)
        {
            return (id, false);
        }

        var tenant = await context.Tenants.AsNoTracking().Where(row => row.Id == tenantId).Select(row => new { row.Id, row.Name }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A Company root is ensured only for an existing tenant.");

        var units = await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.TenantId == tenantId)
            .Select(unit => new { unit.Id, unit.TenantId, unit.Name, unit.UnitType, unit.ParentId })
            .ToListAsync(cancellationToken);
        var root = New(tenant.Id, string.IsNullOrWhiteSpace(name) ? tenant.Name : name, SystemAccount.IdFor(tenant.Id));
        var structure = OrganizationStructure.Of(units.Select(unit => (unit.Id, unit.TenantId, unit.Name, unit.UnitType, unit.ParentId)));
        if (OrganizationStructurePolicy.FirstNewViolation(structure, structure.WithUnit(Guid.NewGuid(), root.TenantId, root.Name, root.UnitType, parentId: null)) is { } reason)
        {
            throw new InvalidOperationException($"The Company root could not be added: {reason}");
        }

        context.OrganizationUnits.Add(root);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return (root.Id, true);
        }
        catch (DbUpdateException) when (acceptConcurrentRoot)
        {
            context.Entry(root).State = EntityState.Detached;
            if (await FindAsync(context, tenantId, cancellationToken) is { } theirs)
            {
                return (theirs, false);
            }

            throw;
        }
    }

    public static Task<Guid?> FindAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default) =>
        context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.TenantId == tenantId && unit.UnitType == OrganizationUnitType.Company)
            .Select(unit => (Guid?)unit.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static OrganizationUnit New(Guid tenantId, string tenantName, Guid createdById) => new()
    {
        TenantId = tenantId,
        Name = tenantName,
        Description = "The whole company",
        UnitType = OrganizationUnitType.Company,
        ParentId = null,
        State = OrganizationUnitState.Active,
        CreatedById = createdById,
    };
}
