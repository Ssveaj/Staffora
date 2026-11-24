using Microsoft.Data.SqlClient;
using Respawn;
using Respawn.Graph;

namespace Staffora.E2ESetup;

public static class DatabaseReset
{
    public const string MigrationHistoryTable = "__EFMigrationsHistory";

    public static async Task ResetAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        E2EDatabaseGuard.EnsureE2EConnection(connectionString);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = [new Table(MigrationHistoryTable)],
        });

        await respawner.ResetAsync(connection);
    }
}
