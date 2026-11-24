using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Identity;

namespace Staffora.Test.Integration.Infrastructure;

internal static class TenantSweeps
{
    public static IReadOnlyList<(string Entity, Type ClrType, string? Schema, string Table)> TenantTables(DbContext context) =>
        [.. context.Model.GetEntityTypes()
            .Where(type => typeof(ITenantEntity).IsAssignableFrom(type.ClrType) && type.GetTableName() is not null)
            .GroupBy(type => (type.GetSchema(), type.GetTableName()!))
            .Select(group => (group.First().ClrType.Name, group.First().ClrType, group.Key.Item1, group.Key.Item2))];

    public static async Task<Dictionary<string, HashSet<Guid>>> TenantsReadAsync(DbContext reader, CancellationToken cancellationToken)
    {
        var read = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var (entity, clrType, _, _) in TenantTables(reader))
        {
            var tenants = await (Task<List<Guid>>)typeof(TenantSweeps)
                .GetMethod(nameof(TenantIdsOfAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(clrType)
                .Invoke(null, [reader, cancellationToken])!;
            read[entity] = [.. tenants];
        }

        return read;
    }

    public static async Task<List<string>> StrangersAsync(RealDatabase database, IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        var users = context.Model.FindEntityType(typeof(ApplicationUser))!;
        var usersTable = $"[{users.GetSchema() ?? "dbo"}].[{users.GetTableName()}]";
        var tenants = string.Join(", ", tenantIds.Select((_, index) => $"@tenant{index}"));

        var found = new List<string>();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var (entity, _, schema, table) in TenantTables(context))
        {
            foreach (var column in new[] { nameof(ITenantEntity.CreatedById), nameof(ITenantEntity.ModifiedById) })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT COUNT(*), MIN(CONVERT(nvarchar(36), row.[{column}]))
                    FROM [{schema ?? "dbo"}].[{table}] AS row
                    WHERE row.[TenantId] IN ({tenants})
                      AND row.[{column}] IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM {usersTable} AS author WHERE author.[Id] = row.[{column}] AND author.[TenantId] = row.[TenantId])
                    """;
                foreach (var (tenantId, index) in tenantIds.Select((tenantId, index) => (tenantId, index)))
                {
                    command.Parameters.AddWithValue($"@tenant{index}", tenantId);
                }

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                if (reader.GetInt32(0) is var count and > 0)
                {
                    found.Add($"{entity}.{column}: {count} row(s) written by a stranger, such as {reader.GetString(1)}");
                }
            }
        }

        return found;
    }

    private static Task<List<Guid>> TenantIdsOfAsync<TEntity>(DbContext context, CancellationToken cancellationToken) where TEntity : class, ITenantEntity =>
        context.Set<TEntity>().Select(row => row.TenantId).Distinct().ToListAsync(cancellationToken);
}
