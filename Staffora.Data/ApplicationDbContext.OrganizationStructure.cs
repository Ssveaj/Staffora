using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Organization;

namespace Staffora.Data;

public partial class ApplicationDbContext
{
    public const IsolationLevel OrganizationStructureIsolationLevel = IsolationLevel.ReadCommitted;

    public const string PlatformTreeIsRootOnlyReason = "The platform's organization tree is its root alone: it has no other units and no role holdings.";

    private const string StructureSavepoint = "OrganizationStructureCheck";

    public Task LockOrganizationStructureAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        TakeStructureLockAsync(tenantId, synchronously: false, cancellationToken);

    private sealed record StructureWrites(IReadOnlyList<Guid> TenantIds, IReadOnlyList<WrittenUnit> Units, IReadOnlyList<WrittenHolding> Holdings, IReadOnlyList<DeletedUnit> Deleted);

    private sealed record WrittenUnit(Guid Id, Guid TenantId, string Name, OrganizationUnitType Type, Guid? ParentId, OrganizationUnitType? StoredType);

    private sealed record WrittenHolding(Guid Id, Guid TenantId, Guid UnitId);

    private sealed record DeletedUnit(Guid Id, Guid TenantId, string Name);

    private sealed record HoldingOnGrouping(Guid HoldingId, Guid UnitId);

    private sealed record UnitInUse(Guid Id, Guid? ParentId, OrganizationUnitType UnitType, OrganizationUnitState State);

    private async Task<int> SaveCheckingStructureAsync(bool acceptAllChangesOnSuccess, bool synchronously, CancellationToken cancellationToken)
    {
        if (PendingStructureWrites() is not { } writes)
        {
            return synchronously ? base.SaveChanges(acceptAllChangesOnSuccess) : await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var ambient = Database.CurrentTransaction;
        var transaction = ambient ?? (synchronously
            ? Database.BeginTransaction(OrganizationStructureIsolationLevel)
            : await Database.BeginTransactionAsync(OrganizationStructureIsolationLevel, cancellationToken));
        try
        {
            foreach (var tenantId in writes.TenantIds)
            {
                await HoldTenantBeforeLockAsync(tenantId, synchronously, cancellationToken);
            }

            foreach (var tenantId in writes.TenantIds)
            {
                await TakeStructureLockAsync(tenantId, synchronously, cancellationToken);
            }

            if (ambient is not null && synchronously)
            {
                transaction.CreateSavepoint(StructureSavepoint);
            }
            else if (ambient is not null)
            {
                await transaction.CreateSavepointAsync(StructureSavepoint, cancellationToken);
            }

            int saved;
            string? reason;
            try
            {
                reason = await FindDeletedRootAsync(writes, synchronously, cancellationToken);
                if (reason is null)
                {
                    saved = synchronously ? base.SaveChanges(acceptAllChangesOnSuccess: false) : await base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
                    reason = await FindStructureViolationAsync(writes, synchronously, cancellationToken);
                }
                else
                {
                    saved = 0;
                }
            }
            catch when (ambient is not null)
            {
                await RollBackToStructureSavepointAsync(transaction, synchronously);
                throw;
            }

            if (reason is not null)
            {
                if (ambient is null && synchronously)
                {
                    transaction.Rollback();
                }
                else if (ambient is null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                else
                {
                    await RollBackToStructureSavepointAsync(transaction, synchronously);
                }

                IsolationLog().LogWarning("Refused an organization structure write: {Reason} Tenants {TenantIds}.", reason, writes.TenantIds);
                throw new OrganizationStructureException(reason);
            }

            if (ambient is null && synchronously)
            {
                transaction.Commit();
            }
            else if (ambient is null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (acceptAllChangesOnSuccess)
            {
                ChangeTracker.AcceptAllChanges();
            }

            return saved;
        }
        finally
        {
            if (ambient is null && synchronously)
            {
                transaction.Dispose();
            }
            else if (ambient is null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task RollBackToStructureSavepointAsync(IDbContextTransaction transaction, bool synchronously)
    {
        try
        {
            if (synchronously)
            {
                transaction.RollbackToSavepoint(StructureSavepoint);
            }
            else
            {
                await transaction.RollbackToSavepointAsync(StructureSavepoint, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            IsolationLog().LogWarning(exception, "Could not roll an organization structure write back to its savepoint.");
        }
    }

    private StructureWrites? PendingStructureWrites()
    {
        var units = new List<WrittenUnit>();
        var holdings = new List<WrittenHolding>();
        var deleted = new List<DeletedUnit>();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is OrganizationUnit removed && entry.State == EntityState.Deleted)
            {
                deleted.Add(new DeletedUnit(removed.Id, removed.TenantId, removed.Name));
            }
            else if (entry.Entity is OrganizationUnit unit
                && (entry.State == EntityState.Added
                    || entry.State == EntityState.Modified && (entry.Property(nameof(OrganizationUnit.ParentId)).IsModified || entry.Property(nameof(OrganizationUnit.UnitType)).IsModified
                        || !Equals(entry.Property(nameof(OrganizationUnit.State)).OriginalValue, entry.Property(nameof(OrganizationUnit.State)).CurrentValue))))
            {
                var storedType = entry.State == EntityState.Added ? (OrganizationUnitType?)null : (OrganizationUnitType)entry.Property(nameof(OrganizationUnit.UnitType)).OriginalValue!;
                units.Add(new WrittenUnit(unit.Id, unit.TenantId, unit.Name, unit.UnitType, unit.ParentId, storedType));
            }
            else if (entry.Entity is OrganizationUnitUserRole holding
                && (entry.State == EntityState.Added
                    || entry.State == EntityState.Modified && (entry.Property(nameof(OrganizationUnitUserRole.OrganizationUnitId)).IsModified || entry.Property(nameof(OrganizationUnitUserRole.TenantRoleId)).IsModified)))
            {
                holdings.Add(new WrittenHolding(holding.Id, holding.TenantId, holding.OrganizationUnitId));
            }
        }

        if (units.Count == 0 && holdings.Count == 0 && deleted.Count == 0)
        {
            return null;
        }

        var tenantIds = units.Select(unit => unit.TenantId)
            .Concat(holdings.Select(holding => holding.TenantId))
            .Concat(deleted.Select(unit => unit.TenantId))
            .Distinct().Order().ToList();
        return new StructureWrites(tenantIds, units, holdings, deleted);
    }

    private async Task TakeStructureLockAsync(Guid tenantId, bool synchronously, CancellationToken cancellationToken)
    {
        try
        {
            await AppLock.Structure.TakeAsync(this, tenantId, record: null, synchronously, cancellationToken);
        }
        catch (AppLockBusyException busy)
        {
            IsolationLog().LogWarning("The organization structure lock of tenant {TenantId} was not granted.", tenantId);
            throw new OrganizationStructureException(busy.Message);
        }
    }

    private async Task<string?> FindDeletedRootAsync(StructureWrites writes, bool synchronously, CancellationToken cancellationToken)
    {
        if (writes.Deleted.Count == 0)
        {
            return null;
        }

        var names = StructureNames();
        SqlParameter[] Parameters() => [.. writes.Deleted.Select((unit, index) => new SqlParameter($"@unit{index}", unit.Id))];
        var list = string.Join(", ", writes.Deleted.Select((_, index) => $"@unit{index}"));
        var sql = $"""
            SELECT [u].{names.UnitId} AS [Value] FROM {names.Units} AS [u] WITH (READCOMMITTEDLOCK, FORCESEEK)
            WHERE [u].{names.UnitType} = {(int)OrganizationUnitType.Company} AND [u].{names.UnitId} IN ({list})
            """;
        var roots = Database.SqlQueryRaw<Guid>(sql, Parameters().Cast<object>().ToArray());
        if ((synchronously ? roots.ToList() : await roots.ToListAsync(cancellationToken)).Count > 0)
        {
            return OrganizationStructurePolicy.RootIsNeverDeleted;
        }

        var belowSql = $"""
            SELECT [c].{names.UnitParent} AS [Value] FROM {names.Units} AS [c] WITH (READCOMMITTEDLOCK, FORCESEEK)
            WHERE [c].{names.UnitParent} IN ({list}) AND [c].{names.UnitId} NOT IN ({list})
            """;
        var parents = Database.SqlQueryRaw<Guid>(belowSql, Parameters().Cast<object>().ToArray());
        var withUnitsBelow = (synchronously ? parents.ToList() : await parents.ToListAsync(cancellationToken)).ToHashSet();
        return writes.Deleted.FirstOrDefault(unit => withUnitsBelow.Contains(unit.Id)) is { } kept
            ? OrganizationStructurePolicy.UnitsBelowReason(kept.Name)
            : null;
    }

    private async Task<string?> FindStructureViolationAsync(StructureWrites writes, bool synchronously, CancellationToken cancellationToken)
    {
        foreach (var tenantId in writes.TenantIds)
        {
            var units = writes.Units.Where(unit => unit.TenantId == tenantId).ToList();
            var holdings = writes.Holdings.Where(holding => holding.TenantId == tenantId).ToList();
            if (units.Count == 0 && holdings.Count == 0)
            {
                continue;
            }

            var platform = Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.IsRootTenant);
            if ((synchronously ? platform.FirstOrDefault() : await platform.FirstOrDefaultAsync(cancellationToken))
                && (holdings.Count > 0 || units.Any(unit => unit.Type != OrganizationUnitType.Company || unit.ParentId is not null)))
            {
                return PlatformTreeIsRootOnlyReason;
            }

            var unreachable = Database.SqlQueryRaw<Guid>(UnreachableUnitsSql(), new SqlParameter("@tenant", tenantId));
            var cut = (synchronously ? unreachable.ToList() : await unreachable.ToListAsync(cancellationToken)).ToHashSet();
            if (units.FirstOrDefault(unit => cut.Contains(unit.Id)) is { } cycled)
            {
                return OrganizationStructurePolicy.CutOffReason(cycled.Name);
            }

            var onGroupings = Database.SqlQueryRaw<HoldingOnGrouping>(HoldingsOnGroupingsSql(), new SqlParameter("@tenant", tenantId));
            var found = synchronously ? onGroupings.ToList() : await onGroupings.ToListAsync(cancellationToken);
            if (holdings.Any(holding => found.Any(row => row.HoldingId == holding.Id)))
            {
                return OrganizationStructurePolicy.GroupingHoldsNoRoles;
            }

            foreach (var unit in units)
            {
                var held = found.Count(row => row.UnitId == unit.Id);
                if (held > 0)
                {
                    return OrganizationStructurePolicy.CheckTypeChange(unit.Name, unit.StoredType ?? unit.Type, OrganizationUnitType.Grouping, held)
                        ?? OrganizationStructurePolicy.GroupingHoldsNoRoles;
                }
            }

            if (holdings.FirstOrDefault(holding => cut.Contains(holding.UnitId)) is { } stranded)
            {
                var name = OrganizationUnits.AsNoTracking().Where(unit => unit.Id == stranded.UnitId).Select(unit => unit.Name);
                return OrganizationStructurePolicy.CutOffHoldsNoRoles((synchronously ? name.FirstOrDefault() : await name.FirstOrDefaultAsync(cancellationToken)) ?? string.Empty);
            }

            if (holdings.Count > 0 && await FindHoldingOutOfUseAsync(holdings, synchronously, cancellationToken) is { } outOfUse)
            {
                return outOfUse;
            }

            if (holdings.Count > 0 && await HoldsSystemOwnerOffTheRootAsync(holdings, synchronously, cancellationToken))
            {
                return OrganizationStructurePolicy.SystemOwnerOnRootOnly;
            }

            if (units.Count > 0 && await FindPlaceInUseViolationAsync(tenantId, units, synchronously, cancellationToken) is { } placeReason)
            {
                return placeReason;
            }
        }

        return null;
    }

    private async Task<string?> FindPlaceInUseViolationAsync(Guid tenantId, IReadOnlyList<WrittenUnit> units, bool synchronously, CancellationToken cancellationToken)
    {
        var names = StructureNames();
        var sql = $"""
            SELECT [u].{names.UnitId} AS [Id], [u].{names.UnitParent} AS [ParentId], [u].{names.UnitType} AS [UnitType], [u].{names.UnitState} AS [State]
            FROM {names.Units} AS [u] WITH (READCOMMITTEDLOCK, FORCESEEK)
            WHERE [u].{names.UnitTenant} = @tenant
            """;
        var query = Database.SqlQueryRaw<UnitInUse>(sql, new SqlParameter("@tenant", tenantId));
        var tree = (synchronously ? query.ToList() : await query.ToListAsync(cancellationToken)).ToDictionary(unit => unit.Id);
        var below = tree.Values.Where(unit => unit.ParentId is not null).ToLookup(unit => unit.ParentId!.Value);
        foreach (var written in units)
        {
            if (!tree.TryGetValue(written.Id, out var unit))
            {
                continue;
            }

            if (unit.UnitType == OrganizationUnitType.Company)
            {
                if (unit.State != OrganizationUnitState.Active)
                {
                    return OrganizationStructurePolicy.RootStaysActive;
                }

                continue;
            }

            if (unit.ParentId is { } parentId && tree.TryGetValue(parentId, out var parent)
                && OrganizationStructurePolicy.CheckPlaceInUse(written.Name, unit.State, parent.State, parent.UnitType) is { } aboveReason)
            {
                return aboveReason;
            }

            foreach (var child in below[unit.Id])
            {
                if (unit.State != OrganizationUnitState.Active && child.State == OrganizationUnitState.Active)
                {
                    return OrganizationStructurePolicy.OutOfUseAboveInUseReason(written.Name);
                }

                if (unit.State == OrganizationUnitState.Archived && child.State != OrganizationUnitState.Archived)
                {
                    return OrganizationStructurePolicy.ArchivedAboveUnarchivedReason(written.Name);
                }
            }
        }

        return null;
    }

    private async Task<string?> FindHoldingOutOfUseAsync(IReadOnlyList<WrittenHolding> holdings, bool synchronously, CancellationToken cancellationToken)
    {
        var names = StructureNames();
        var unitIds = holdings.Select(holding => holding.UnitId).Distinct().ToList();
        var list = string.Join(", ", unitIds.Select((_, index) => $"@unit{index}"));
        var sql = $"""
            SELECT [u].{names.UnitId} AS [Id], [u].{names.UnitParent} AS [ParentId], [u].{names.UnitType} AS [UnitType], [u].{names.UnitState} AS [State]
            FROM {names.Units} AS [u] WITH (READCOMMITTEDLOCK, FORCESEEK)
            WHERE [u].{names.UnitId} IN ({list})
            """;
        var query = Database.SqlQueryRaw<UnitInUse>(sql, [.. unitIds.Select((id, index) => (object)new SqlParameter($"@unit{index}", id))]);
        var units = synchronously ? query.ToList() : await query.ToListAsync(cancellationToken);
        if (units.FirstOrDefault(unit => unit.State != OrganizationUnitState.Active && unit.UnitType != OrganizationUnitType.Company) is not { } outOfUse)
        {
            return null;
        }

        var name = OrganizationUnits.AsNoTracking().Where(unit => unit.Id == outOfUse.Id).Select(unit => unit.Name);
        return OrganizationStructurePolicy.OutOfUseHoldsNoRoles((synchronously ? name.FirstOrDefault() : await name.FirstOrDefaultAsync(cancellationToken)) ?? string.Empty);
    }

    private async Task<bool> HoldsSystemOwnerOffTheRootAsync(IReadOnlyList<WrittenHolding> holdings, bool synchronously, CancellationToken cancellationToken)
    {
        var ids = holdings.Select(holding => holding.Id).ToList();
        var offTheRoot = OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => ids.Contains(holding.Id) && holding.TenantRole.IsSystemOwner && holding.OrganizationUnit.UnitType != OrganizationUnitType.Company);
        return synchronously ? offTheRoot.Any() : await offTheRoot.AnyAsync(cancellationToken);
    }

    private sealed record StructureTableNames(string Units, string UnitId, string UnitParent, string UnitTenant, string UnitType, string UnitState, string Holdings, string HoldingId, string HoldingUnit, string HoldingTenant);

    private StructureTableNames StructureNames()
    {
        var sql = this.GetService<ISqlGenerationHelper>();
        (string Table, Func<string, string> Column) Of<TEntity>()
        {
            var entity = Model.FindEntityType(typeof(TEntity))!;
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            return (sql.DelimitIdentifier(table.Name, table.Schema), property => sql.DelimitIdentifier(entity.FindProperty(property)!.GetColumnName(table)!));
        }

        var (units, unit) = Of<OrganizationUnit>();
        var (holdings, holding) = Of<OrganizationUnitUserRole>();
        return new StructureTableNames(
            units, unit(nameof(OrganizationUnit.Id)), unit(nameof(OrganizationUnit.ParentId)), unit(nameof(OrganizationUnit.TenantId)), unit(nameof(OrganizationUnit.UnitType)), unit(nameof(OrganizationUnit.State)),
            holdings, holding(nameof(OrganizationUnitUserRole.Id)), holding(nameof(OrganizationUnitUserRole.OrganizationUnitId)), holding(nameof(OrganizationUnitUserRole.TenantId)));
    }

    private string UnreachableUnitsSql()
    {
        var names = StructureNames();
        var (units, id, parent, tenant) = (names.Units, names.UnitId, names.UnitParent, names.UnitTenant);
        return $"""
            WITH [Reached] AS (
                SELECT [u].{id} AS [Id] FROM {units} AS [u] WITH (READCOMMITTEDLOCK, FORCESEEK) WHERE [u].{tenant} = @tenant AND [u].{parent} IS NULL
                UNION ALL
                SELECT [c].{id} FROM {units} AS [c] WITH (READCOMMITTEDLOCK, FORCESEEK) INNER JOIN [Reached] AS [r] ON [c].{parent} = [r].[Id] WHERE [c].{tenant} = @tenant
            )
            SELECT [u].{id} AS [Value] FROM {units} AS [u] WITH (READCOMMITTEDLOCK, FORCESEEK)
            WHERE [u].{tenant} = @tenant AND NOT EXISTS (SELECT 1 FROM [Reached] AS [r] WHERE [r].[Id] = [u].{id})
            OPTION (MAXRECURSION 32767)
            """;
    }

    private string HoldingsOnGroupingsSql()
    {
        var names = StructureNames();
        return $"""
            SELECT [h].{names.HoldingId} AS [HoldingId], [h].{names.HoldingUnit} AS [UnitId]
            FROM {names.Holdings} AS [h] WITH (READCOMMITTEDLOCK, FORCESEEK)
            INNER JOIN {names.Units} AS [u] WITH (READCOMMITTEDLOCK, FORCESEEK) ON [u].{names.UnitId} = [h].{names.HoldingUnit}
            WHERE [h].{names.HoldingTenant} = @tenant AND [u].{names.UnitType} = {(int)OrganizationUnitType.Grouping}
            """;
    }

    internal OrganizationStructureException RefuseBulkStructureWrite(string entity, string column)
    {
        IsolationLog().LogWarning("Refused a bulk update of {EntityType}.{Column}: the organization tree's shape changes only through SaveChanges.", entity, column);
        return new OrganizationStructureException(BulkStructureWriteReason(entity, column));
    }

    internal static string BulkStructureWriteReason(string entity, string column) =>
        $"Refused a bulk update of {entity}.{column}: the organization tree's shape changes only through SaveChanges, which checks it under the tenant's structure lock.";

    internal OrganizationStructureException RefuseBulkUnitDelete()
    {
        IsolationLog().LogWarning("Refused a bulk delete of {EntityType}: a unit is removed only through SaveChanges, which keeps the Company root.", nameof(OrganizationUnit));
        return new OrganizationStructureException(BulkUnitDeleteReason);
    }

    internal const string BulkUnitDeleteReason =
        "Refused a bulk delete of OrganizationUnit: a unit is removed only through SaveChanges, which checks under the tenant's structure lock that the Company root stays.";
}
