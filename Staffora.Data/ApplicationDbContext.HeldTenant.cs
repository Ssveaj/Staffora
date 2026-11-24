using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data;

public partial class ApplicationDbContext
{
    private TenantHold? tenantHold;

    private static string? tenantHoldSql;

    private sealed record TenantHold(DbTransaction Transaction, Guid TenantId, TenantState? State);

    private Guid? HeldWriteTenant() => IsInSystemScope ? null : RuntimeContext?.Current.TenantId;

    internal TenantPurgeLease? PurgeLease { get; set; }

    internal TenantIsolationException RefuseTenantPurge(Guid? tenantId, string reason)
    {
        IsolationLog().LogWarning("Refused a tenant purge of tenant {TenantId}: {Reason}", tenantId, reason);
        return new TenantIsolationException($"Refused a tenant purge: {reason}");
    }

    internal void RecordTenantPurge(string change, Guid tenantId) =>
        IsolationLog().LogInformation("Tenant purge {Change} for tenant {TenantId}.", change, tenantId);

    public Task<TenantState?> HoldTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var transaction = Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A tenant's state is held for a transaction: begin one before reading it.");
        return ReadTenantHoldAsync(Database.GetDbConnection(), transaction, tenantId, synchronously: false, cancellationToken);
    }

    internal async Task HoldTenantBeforeLockAsync(Guid tenantId, bool synchronously, CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction?.GetDbTransaction() is { } transaction)
        {
            await ReadTenantHoldAsync(Database.GetDbConnection(), transaction, tenantId, synchronously, cancellationToken);
        }
    }

    private Guid? heldSaveTenant;

    internal async Task HoldOnTransactionStartAsync(DbConnection connection, DbTransaction transaction, bool synchronously, CancellationToken cancellationToken)
    {
        if (HeldWriteTenant() is { } tenantId)
        {
            await ReadTenantHoldAsync(connection, transaction, tenantId, synchronously, cancellationToken);
        }

        if (this.heldSaveTenant is { } saving)
        {
            var state = await ReadTenantHoldAsync(connection, transaction, saving, synchronously, cancellationToken);
            if (TenantStates.HoldsData(state))
            {
                RefuseHeldSave(saving, state);
            }
        }
    }

    private IEnumerable<EntityEntry> PendingTenantData() =>
        ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && entry.Entity is not Tenant
            && (entry.Entity is Staffora.Core.Entities.ITenantEntity || IsAttributeDefinition(entry.Metadata.ClrType) || IsAttributeValue(entry.Metadata.ClrType)));

    private async Task RefuseWritesOfHeldTenantAsync(Guid? runtimeTenant, bool systemScope, bool synchronously, CancellationToken cancellationToken)
    {
        this.heldSaveTenant = null;
        if (systemScope || runtimeTenant is not { } tenantId || !PendingTenantData().Any())
        {
            return;
        }

        if (Database.CurrentTransaction?.GetDbTransaction() is not { } transaction)
        {
            this.heldSaveTenant = tenantId;
            return;
        }

        var state = await ReadTenantHoldAsync(Database.GetDbConnection(), transaction, tenantId, synchronously, cancellationToken);
        if (TenantStates.HoldsData(state))
        {
            RefuseHeldSave(tenantId, state);
        }
    }

    private void RefuseHeldSave(Guid tenantId, TenantState? state)
    {
        var (operation, entity, key) = PendingTenantData().FirstOrDefault() is { } write ? Describe(write) : ("write", "data", string.Empty);
        IsolationLog().LogWarning(
            "Refused tenant {Operation} on {EntityType} {Key}: the tenant's data is held ({TenantState}). Runtime tenant {RuntimeTenantId}.",
            operation, entity, key, state?.ToString() ?? "not found", tenantId);
        throw new TenantHeldException();
    }

    internal async Task<InterceptionResult<int>> JudgeBulkStatementAsync(DbCommand command, InterceptionResult<int> result, bool synchronously, CancellationToken cancellationToken)
    {
        if (HeldWriteTenant() is not { } tenantId || command.Connection is not { } connection)
        {
            return result;
        }

        if (command.Transaction is { } ambient)
        {
            RefuseBulkWhenHeld(await ReadTenantHoldAsync(connection, ambient, tenantId, synchronously, cancellationToken), tenantId);
            return result;
        }

        var own = synchronously ? connection.BeginTransaction(IsolationLevel.ReadCommitted) : await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            RefuseBulkWhenHeld(await ReadTenantHoldAsync(connection, own, tenantId, synchronously, cancellationToken), tenantId);
            command.Transaction = own;
            var rows = synchronously ? command.ExecuteNonQuery() : await command.ExecuteNonQueryAsync(cancellationToken);
            if (synchronously)
            {
                own.Commit();
            }
            else
            {
                await own.CommitAsync(cancellationToken);
            }

            return InterceptionResult<int>.SuppressWithResult(rows);
        }
        finally
        {
            command.Transaction = null;
            if (synchronously)
            {
                own.Dispose();
            }
            else
            {
                await own.DisposeAsync();
            }
        }
    }

    private void RefuseBulkWhenHeld(TenantState? state, Guid tenantId)
    {
        if (TenantStates.HoldsData(state))
        {
            IsolationLog().LogWarning("Refused a bulk statement: the tenant's data is held ({TenantState}). Runtime tenant {RuntimeTenantId}.", state?.ToString() ?? "not found", tenantId);
            throw new TenantHeldException();
        }
    }

    private async Task<TenantState?> ReadTenantHoldAsync(DbConnection connection, DbTransaction transaction, Guid tenantId, bool synchronously, CancellationToken cancellationToken)
    {
        if (this.tenantHold is { } held && ReferenceEquals(held.Transaction, transaction) && held.TenantId == tenantId)
        {
            return held.State;
        }

        var state = await ReadTenantStateAsync(connection, transaction, tenantId, synchronously, cancellationToken);
        this.tenantHold = new TenantHold(transaction, tenantId, state);
        return state;
    }

    private async Task<TenantState?> ReadTenantStateAsync(DbConnection connection, DbTransaction? transaction, Guid tenantId, bool synchronously, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            if (synchronously)
            {
                Database.OpenConnection();
            }
            else
            {
                await Database.OpenConnectionAsync(cancellationToken);
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = tenantHoldSql ??= TenantHoldSql();
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.DbType = DbType.Guid;
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);
        var value = synchronously ? command.ExecuteScalar() : await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (TenantState)Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private string TenantHoldSql()
    {
        var type = Model.FindEntityType(typeof(Tenant)) ?? throw new InvalidOperationException("The model maps no Tenant.");
        var table = StoreObjectIdentifier.Table(type.GetTableName()!, type.GetSchema());
        var name = table.Schema is { } schema ? $"[{schema}].[{table.Name}]" : $"[{table.Name}]";
        var state = type.FindProperty(nameof(Tenant.State))!.GetColumnName(table);
        var id = type.FindProperty(nameof(Tenant.Id))!.GetColumnName(table);
        return $"SELECT [{state}] FROM {name} WITH (HOLDLOCK, ROWLOCK) WHERE [{id}] = @tenant";
    }
}
