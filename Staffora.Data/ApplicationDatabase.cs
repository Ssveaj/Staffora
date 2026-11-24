using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Staffora.Data;

public static class ApplicationDatabase
{
    public const string VersionedReadsSql =
        "IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0) EXEC (N'ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE')";

    public static async Task MigrateAsync(DatabaseFacade database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.MigrateAsync(cancellationToken);
        await database.ExecuteSqlRawAsync(VersionedReadsSql, cancellationToken);
    }
}
