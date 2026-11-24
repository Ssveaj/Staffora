using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

[assembly: AssemblyFixture(typeof(DatabasePool))]

namespace Staffora.Test.Integration.Infrastructure;

public enum TemplatePreparation
{
    Migrated = 0,

    Reused = 1,
}

public sealed record TemplateReport(TemplatePreparation Preparation, string Fingerprint, TimeSpan Duration);

public sealed class DatabasePool : IAsyncDisposable
{
    public const string ConnectionVariable = "STAFFORA_INTEGRATION_CONNECTION";

    public const string FilesVariable = "STAFFORA_INTEGRATION_FILES";

    public const string DefaultConnection = @"Server=(localdb)\MSSQLLocalDB;Database=Staffora_Integration_test;Trusted_Connection=True;TrustServerCertificate=True";

    private const int LongCommandSeconds = 300;

    private readonly SqlConnectionStringBuilder server;
    private readonly string templateName;
    private readonly Lazy<Task<TemplateReport>> template;
    private readonly ConcurrentDictionary<int, byte> slotsInUse = new();

    public DatabasePool()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        var connection = DatabaseNameGuard.EnsureTestConnection(string.IsNullOrWhiteSpace(configured) ? DefaultConnection : configured);
        this.server = new SqlConnectionStringBuilder(connection);
        BaseName = this.server.InitialCatalog;
        this.templateName = TemplateName(BaseName);

        this.template = new Lazy<Task<TemplateReport>>(() => PrepareTemplateAsync(CancellationToken.None));
    }

    public string BaseName { get; }

    public Task<TemplateReport> TemplateAsync => this.template.Value;

    public static string TemplateName(string baseName) => DatabaseNameGuard.EnsureTestDatabase($"{baseName}_template");

    public static string SlotName(string baseName, int slot) => DatabaseNameGuard.EnsureTestDatabase($"{baseName}_{slot}");

    public static string IsolatedPrefix(string baseName) => $"{baseName}_isolated_";

    public static string IsolatedName(string baseName, string purpose)
    {
        if (purpose.Length is 0 or > 32 || !purpose.All(char.IsAsciiLetterLower))
        {
            throw new ArgumentException("The purpose of an isolated database is one word of at most 32 lower-case letters.", nameof(purpose));
        }

        return DatabaseNameGuard.EnsureTestDatabase($"{IsolatedPrefix(baseName)}{purpose}_{Guid.NewGuid():N}"[..^20]);
    }

    public static string Fingerprint()
    {
        using var context = new TestDataContext(ApplicationDbContextOptions.Create(DefaultConnection));
        var input = $"{typeof(ApplicationDbContext).Assembly.ManifestModule.ModuleVersionId}\n{context.Database.GenerateCreateScript()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    internal async Task<TemplateReport> PrepareTemplateAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var fingerprint = Fingerprint();

        await using var master = await OpenMasterAsync(cancellationToken);
        await LockAsync(master, $"Staffora.Test template {this.templateName}", timeoutMilliseconds: 240_000, cancellationToken);

        var files = await FilesFolderAsync(master, cancellationToken);
        var backup = ServerPath(files, $"{this.templateName}_{fingerprint}.bak");
        if (await BackupFilesAsync(master, backup, cancellationToken) is not null)
        {
            await DropAsync(master, this.templateName, cancellationToken);
            await DropAbandonedIsolatedAsync(master, cancellationToken);
            return new TemplateReport(TemplatePreparation.Reused, fingerprint, watch.Elapsed);
        }

        await DropAsync(master, this.templateName, cancellationToken);
        await DropAbandonedIsolatedAsync(master, cancellationToken);
        await CreateAsync(master, this.templateName, files, cancellationToken);
        await MigrateAsync(ConnectionTo(this.templateName), cancellationToken);
        await ExecuteAsync(master, "BACKUP DATABASE @name TO DISK = @backup WITH INIT, FORMAT, COPY_ONLY", cancellationToken, ("@name", this.templateName), ("@backup", backup));
        DeleteOlderBackups(files, backup);

        ClearPool(ConnectionTo(this.templateName));
        await DropAsync(master, this.templateName, cancellationToken);

        return new TemplateReport(TemplatePreparation.Migrated, fingerprint, watch.Elapsed);
    }

    internal async Task<DatabaseLease> LeaseAsync(CancellationToken cancellationToken)
    {
        var template = await TemplateAsync.WaitAsync(cancellationToken);

        for (var slot = 1; ; slot++)
        {
            if (!this.slotsInUse.TryAdd(slot, 0))
            {
                continue;
            }

            var name = SlotName(BaseName, slot);
            var holder = await OpenMasterAsync(cancellationToken);
            try
            {
                if (await TryLockAsync(holder, $"Staffora.Test database {name}", cancellationToken))
                {
                    await RestoreAsync(holder, name, template.Fingerprint, cancellationToken);
                    return new DatabaseLease(this, slot: slot, name, ConnectionTo(name), holder);
                }
            }
            catch
            {
                await holder.DisposeAsync();
                this.slotsInUse.TryRemove(slot, out _);
                throw;
            }

            await holder.DisposeAsync();
            this.slotsInUse.TryRemove(slot, out _);
        }
    }

    internal async Task<DatabaseLease> LeaseIsolatedAsync(string purpose, CancellationToken cancellationToken)
    {
        var template = await TemplateAsync.WaitAsync(cancellationToken);
        var name = IsolatedName(BaseName, purpose);
        var holder = await OpenMasterAsync(cancellationToken);
        try
        {
            await LockAsync(holder, $"Staffora.Test database {name}", timeoutMilliseconds: 0, cancellationToken);
            await RestoreAsync(holder, name, template.Fingerprint, cancellationToken);

            var connection = new SqlConnectionStringBuilder(ConnectionTo(name)) { ApplicationName = $"Staffora.Test {name}" }.ConnectionString;
            return new DatabaseLease(this, slot: null, name, connection, holder);
        }
        catch
        {
            await DropAsync(holder, name, CancellationToken.None);
            await holder.DisposeAsync();
            throw;
        }
    }

    internal async ValueTask ReleaseAsync(DatabaseLease lease)
    {
        ClearPool(lease.ConnectionString);
        if (lease.Slot is { } slot)
        {
            await lease.Holder.DisposeAsync();
            this.slotsInUse.TryRemove(slot, out _);
            return;
        }

        try
        {
            await DropAsync(lease.Holder, lease.Name, CancellationToken.None);
        }
        finally
        {
            await lease.Holder.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private string ConnectionTo(string database) =>
        DatabaseNameGuard.EnsureTestConnection(new SqlConnectionStringBuilder(this.server.ConnectionString) { InitialCatalog = database }.ConnectionString);

    private static void ClearPool(string connectionString)
    {
        using var poolKey = new SqlConnection(connectionString);
        SqlConnection.ClearPool(poolKey);
    }

    private async Task<SqlConnection> OpenMasterAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(this.server.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException exception)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                $"Real-database tests could not connect to the SQL Server '{this.server.DataSource}'. Start it, or set {ConnectionVariable} to a connection string for a reachable server whose database name has \"{DatabaseNameGuard.Marker}\" as a word of its own. {exception.Message}",
                exception);
        }
    }

    private async Task RestoreAsync(SqlConnection master, string name, string fingerprint, CancellationToken cancellationToken)
    {
        var files = await FilesFolderAsync(master, cancellationToken);
        var backup = ServerPath(files, $"{this.templateName}_{fingerprint}.bak");
        var logicalFiles = await BackupFilesAsync(master, backup, cancellationToken)
            ?? throw new InvalidOperationException($"The template backup {backup} is missing or unreadable.");

        await ExecuteAsync(master, """
            DECLARE @sql nvarchar(max);
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                SET @sql = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE';
                EXEC (@sql);
            END
            RESTORE DATABASE @name FROM DISK = @backup WITH REPLACE, RECOVERY, MOVE @dataName TO @dataFile, MOVE @logName TO @logFile;
            SET @sql = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET MULTI_USER';
            EXEC (@sql);
            """,
            cancellationToken,
            ("@name", DatabaseNameGuard.EnsureTestDatabase(name)),
            ("@backup", backup),
            ("@dataName", logicalFiles.Data),
            ("@dataFile", ServerPath(files, $"{name}.mdf")),
            ("@logName", logicalFiles.Log),
            ("@logFile", ServerPath(files, $"{name}_log.ldf")));
    }

    private static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        IModel applicationModel;
        await using (var application = new ApplicationDbContext(ApplicationDbContextOptions.Create(connectionString)))
        {
            application.Database.SetCommandTimeout(LongCommandSeconds);
            await ApplicationDatabase.MigrateAsync(application.Database, cancellationToken);
            applicationModel = application.GetService<IDesignTimeModel>().Model;
        }

        await using var test = new TestDataContext(ApplicationDbContextOptions.Create(connectionString));
        test.Database.SetCommandTimeout(LongCommandSeconds);
        var testModel = test.GetService<IDesignTimeModel>().Model;
        var applicationTables = applicationModel.GetRelationalModel().Tables.Select(table => table.Name).ToHashSet(StringComparer.Ordinal);

        var operations = test.GetService<IMigrationsModelDiffer>().GetDifferences(applicationModel.GetRelationalModel(), testModel.GetRelationalModel());
        var refused = operations
            .Where(operation => operation switch
            {
                CreateTableOperation table => applicationTables.Contains(table.Name),
                CreateIndexOperation index => applicationTables.Contains(index.Table),
                _ => true,
            })
            .Select(operation => operation.GetType().Name)
            .ToList();
        if (refused.Count > 0)
        {
            throw new InvalidOperationException($"The test model may only add tables to the application's schema, but it also needs: {string.Join(", ", refused)}.");
        }

        var commands = test.GetService<IMigrationsSqlGenerator>().Generate(operations, testModel);
        await test.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(commands, test.GetService<IRelationalConnection>(), cancellationToken);
    }

    private async Task<string> FilesFolderAsync(SqlConnection master, CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable(FilesVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (this.server.DataSource.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Staffora.Test", "Databases");
            Directory.CreateDirectory(folder);
            return folder;
        }

        await using var command = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000))", master);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(string Data, string Log)?> BackupFilesAsync(SqlConnection master, string backup, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @backup", master) { CommandTimeout = LongCommandSeconds };
            command.Parameters.AddWithValue("@backup", backup);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            string? data = null, log = null;
            while (await reader.ReadAsync(cancellationToken))
            {
                var logicalName = reader.GetString(reader.GetOrdinal("LogicalName"));
                if (reader.GetString(reader.GetOrdinal("Type")) == "L")
                {
                    log = logicalName;
                }
                else
                {
                    data = logicalName;
                }
            }

            return data is not null && log is not null ? (data, log) : null;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static async Task CreateAsync(SqlConnection master, string name, string files, CancellationToken cancellationToken)
    {
        await ExecuteAsync(master, """
            DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name)
                + N' ON (NAME = ' + QUOTENAME(@name, '''') + N', FILENAME = ' + QUOTENAME(@dataFile, '''') + N')'
                + N' LOG ON (NAME = ' + QUOTENAME(@name + N'_log', '''') + N', FILENAME = ' + QUOTENAME(@logFile, '''') + N')';
            EXEC (@sql);
            """,
            cancellationToken,
            ("@name", DatabaseNameGuard.EnsureTestDatabase(name)),
            ("@dataFile", ServerPath(files, $"{name}.mdf")),
            ("@logFile", ServerPath(files, $"{name}_log.ldf")));
    }

    private async Task DropAbandonedIsolatedAsync(SqlConnection master, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await using (var command = new SqlCommand("SELECT name FROM sys.databases", master))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                if (name.StartsWith(IsolatedPrefix(BaseName), StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }

        foreach (var name in names)
        {
            await using var holder = await OpenMasterAsync(cancellationToken);
            if (await TryLockAsync(holder, $"Staffora.Test database {name}", cancellationToken))
            {
                await DropAsync(holder, name, cancellationToken);
            }
        }
    }

    private static async Task DropAsync(SqlConnection master, string name, CancellationToken cancellationToken)
    {
        await ExecuteAsync(master, """
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
            END
            """,
            cancellationToken,
            ("@name", DatabaseNameGuard.EnsureTestDatabase(name)));
    }

    private static async Task LockAsync(SqlConnection connection, string resource, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (await GetApplicationLockAsync(connection, resource, timeoutMilliseconds, cancellationToken) < 0)
        {
            throw new TimeoutException($"Another test run held the lock '{resource}' for longer than {timeoutMilliseconds} ms.");
        }
    }

    private static async Task<bool> TryLockAsync(SqlConnection connection, string resource, CancellationToken cancellationToken) =>
        await GetApplicationLockAsync(connection, resource, timeoutMilliseconds: 0, cancellationToken) >= 0;

    private static async Task<int> GetApplicationLockAsync(SqlConnection connection, string resource, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("sp_getapplock", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = LongCommandSeconds };
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", timeoutMilliseconds);
        var result = command.Parameters.Add("@Result", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (int)result.Value;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = LongCommandSeconds };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(name, SqlDbType.NVarChar, 4000).Value = value;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void DeleteOlderBackups(string files, string current)
    {
        if (!Directory.Exists(files))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(files, $"{this.templateName}_*.bak").Where(file => !string.Equals(Path.GetFullPath(file), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(file);
        }
    }

    private static string ServerPath(string folder, string file)
    {
        var separator = folder.Contains('\\') ? '\\' : '/';
        return $"{folder.TrimEnd('\\', '/')}{separator}{file}";
    }
}

internal sealed class DatabaseLease : IAsyncDisposable
{
    private readonly DatabasePool pool;

    public DatabaseLease(DatabasePool pool, int? slot, string name, string connectionString, SqlConnection holder)
    {
        this.pool = pool;
        Slot = slot;
        Name = name;
        ConnectionString = connectionString;
        Holder = holder;
    }

    public int? Slot { get; }

    public string Name { get; }

    public string ConnectionString { get; }

    public SqlConnection Holder { get; }

    public ValueTask DisposeAsync() => this.pool.ReleaseAsync(this);
}
