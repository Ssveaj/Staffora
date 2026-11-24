using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Data;

namespace Staffora.Test.Integration.Infrastructure;

[Trait("Category", "Integration")]
public class TemplateDatabaseTests : IClassFixture<RealDatabase>
{
    private readonly DatabasePool pool;
    private readonly RealDatabase database;

    public TemplateDatabaseTests(DatabasePool pool, RealDatabase database)
    {
        this.pool = pool;
        this.database = database;
    }

    [Fact]
    public async Task A_second_preparation_with_unchanged_migrations_skips_migrating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await this.pool.TemplateAsync;

        var second = await this.pool.PrepareTemplateAsync(cancellationToken);

        TestContext.Current.TestOutputHelper?.WriteLine($"This run's first preparation: {first.Preparation} in {first.Duration.TotalSeconds:0.0} s (fingerprint {first.Fingerprint}).");
        Assert.Equal(TemplatePreparation.Reused, second.Preparation);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task The_class_database_is_a_restored_copy_of_the_migrated_template()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var application = new ApplicationDbContext(ApplicationDbContextOptions.Create(this.database.ConnectionString));
        await using var context = this.database.CreateSystemContext();
        var applied = await application.Database.GetAppliedMigrationsAsync(cancellationToken);

        Assert.NotEqual(DatabasePool.TemplateName(this.pool.BaseName), this.database.Name);
        Assert.Single(applied);
        Assert.Equal(application.Database.GetMigrations(), applied);
        Assert.Empty(await application.Database.GetPendingMigrationsAsync(cancellationToken));
        Assert.Equal(0, await context.TestParents.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task An_isolated_database_is_a_restored_copy_with_a_pool_of_its_own_whose_options_never_reach_the_class_database_and_is_dropped_when_disposed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string name;
        await using (var isolated = await this.database.CreateIsolatedAsync("probe", cancellationToken))
        {
            name = isolated.Name;
            Assert.StartsWith(DatabasePool.IsolatedPrefix(this.pool.BaseName), name);
            Assert.True(DatabaseNameGuard.IsTestDatabase(name));

            var connection = new SqlConnectionStringBuilder(isolated.ConnectionString);
            Assert.Equal($"Staffora.Test {name}", connection.ApplicationName);
            Assert.NotEqual(new SqlConnectionStringBuilder(this.database.ConnectionString).ApplicationName, connection.ApplicationName);

            await using (var application = new ApplicationDbContext(ApplicationDbContextOptions.Create(isolated.ConnectionString)))
            {
                Assert.Equal(application.Database.GetMigrations(), await application.Database.GetAppliedMigrationsAsync(cancellationToken));
            }

            await using (var alter = new SqlConnection(new SqlConnectionStringBuilder(isolated.ConnectionString) { Pooling = false }.ConnectionString))
            {
                await alter.OpenAsync(cancellationToken);
                await using var command = new SqlCommand("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON", alter);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            Assert.Equal(1, await SnapshotIsolationStateAsync(name, cancellationToken));
            Assert.Equal(0, await SnapshotIsolationStateAsync(this.database.Name, cancellationToken));
        }

        Assert.Null(await SnapshotIsolationStateAsync(name, cancellationToken));
    }

    private async Task<int?> SnapshotIsolationStateAsync(string name, CancellationToken cancellationToken)
    {
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(this.database.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        await master.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT CAST(snapshot_isolation_state AS int) FROM sys.databases WHERE name = @name", master);
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync(cancellationToken) is int state ? state : null;
    }
}
