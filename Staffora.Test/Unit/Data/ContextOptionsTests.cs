using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Staffora.Core.Entities.Identity;
using Staffora.Data;
using Staffora.Data.Seed;
using Staffora.Test.Docs;

namespace Staffora.Test.Unit.Data;

public class ContextOptionsTests
{
    [Fact]
    public void Only_the_shared_options_method_configures_the_provider()
    {
        var callers = RepoFiles.CommittedTextFiles(".cs")
            .Select(file => (File: file, Path: Path.GetRelativePath(RepoFiles.Root, file).Replace('\\', '/')))
            .Where(source => !source.Path.StartsWith("Staffora.Test/", StringComparison.Ordinal))
            .Where(source => File.ReadAllText(source.File).Contains(".UseSqlServer(", StringComparison.Ordinal))
            .Select(source => source.Path);

        Assert.Equal(["Staffora.Data/ApplicationDbContextOptions.cs"], callers);
    }

    [Fact]
    public void The_app_builds_its_options_through_the_shared_method()
    {
        var program = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "Program.cs"));
        var registration = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Data", "ApplicationDbContextRegistration.cs"));

        Assert.Contains(".AddApplicationDbContexts(", program, StringComparison.Ordinal);
        Assert.Contains(".UseApplicationSqlServer(", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void The_design_time_factory_creates_a_SQL_Server_context_from_Staffora_Data()
    {
        const string connection = ModelUnderTest.UnopenedConnection;

        using var context = new DesignTimeApplicationDbContextFactory().CreateDbContext([DesignTimeApplicationDbContextFactory.ConnectionArgument, connection]);

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.Equal("Staffora_ModelConventions_test", context.Database.GetDbConnection().Database);
        Assert.Equal(typeof(ApplicationDbContext).Assembly, typeof(DesignTimeApplicationDbContextFactory).Assembly);
    }

    [Fact]
    public void The_note_that_savepoints_are_off_under_MARS_is_logged_at_Debug()
    {
        var options = ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection);

        var warnings = options.FindExtension<CoreOptionsExtension>()!.WarningsConfiguration;

        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, warnings.GetLevel(SqlServerEventId.SavepointsDisabledBecauseOfMARS));
        Assert.Equal(WarningBehavior.Log, warnings.GetBehavior(SqlServerEventId.SavepointsDisabledBecauseOfMARS));
    }

    [Fact]
    public void The_design_time_factory_refuses_a_connection_argument_without_a_value()
    {
        Assert.Throws<ArgumentException>(() => DesignTimeApplicationDbContextFactory.ResolveConnectionString([DesignTimeApplicationDbContextFactory.ConnectionArgument]));
    }

    [Fact]
    public void The_model_built_outside_the_app_has_no_changes_the_migrations_lack()
    {
        using var context = new ApplicationDbContext(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void The_app_builds_its_Identity_schema_from_the_shared_version()
    {
        var program = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "Program.cs"));

        Assert.Contains("options.Stores.SchemaVersion = ApplicationDbContextOptions.IdentitySchemaVersion;", program, StringComparison.Ordinal);

        Assert.Equal(IdentitySchemaVersions.Version2, ApplicationDbContextOptions.IdentitySchemaVersion);
        Assert.Null(ModelUnderTest.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>)));
    }

    [Fact]
    public void The_app_requires_email_addresses_unique_across_every_tenant()
    {
        var program = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "Program.cs"));
        Assert.Contains("options.User.RequireUniqueEmail = true;", program, StringComparison.Ordinal);

        var index = Assert.Single(
            ModelUnderTest.Model.FindEntityType(typeof(ApplicationUser))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([nameof(ApplicationUser.NormalizedEmail)]));
        Assert.Equal((true, "[NormalizedEmail] IS NOT NULL"), (index.IsUnique, index.GetFilter()));
    }

    [Fact]
    public void The_bootstrap_checks_its_address_before_the_database_is_migrated()
    {
        var program = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "Program.cs"));
        var check = program.IndexOf("PlatformBootstrap.ParseEmail(", StringComparison.Ordinal);
        Assert.InRange(check, 0, program.IndexOf("MigrateAsync(", StringComparison.Ordinal));
        Assert.True(check < program.IndexOf("WebApplication.CreateBuilder(", StringComparison.Ordinal));

        Assert.Equal("owner@example.com", PlatformBootstrap.ParseEmail(" owner@example.com "));
        Assert.All(new[] { null, "", "owner", "Owner <owner@example.com>", "owner@example.com extra" }, email => Assert.Null(PlatformBootstrap.ParseEmail(email)));
    }

    [Fact]
    public void The_unordered_row_limit_switch_turns_EFs_warning_into_an_exception()
    {
        static WarningBehavior? BehaviorOf(DbContextOptions options) =>
            options.FindExtension<CoreOptionsExtension>()?.WarningsConfiguration.GetBehavior(CoreEventId.RowLimitingOperationWithoutOrderByWarning);

        Assert.Equal(WarningBehavior.Throw, BehaviorOf(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection, throwOnUnorderedRowLimit: true)));
        Assert.NotEqual(WarningBehavior.Throw, BehaviorOf(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection)));
    }

    [Fact]
    public void The_unordered_row_limit_switch_is_on_in_Development_the_preparation_and_the_tests_only()
    {
        var program = File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", "Program.cs"));
        Assert.Contains("builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>(ApplicationDbContextOptions.ThrowOnUnorderedRowLimitSetting)", program, StringComparison.Ordinal);
        Assert.Contains(".AddApplicationDbContexts(connectionString, throwOnUnorderedRowLimit);", program, StringComparison.Ordinal);

        static bool? SwitchIn(string file)
        {
            using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Blazor", file)));
            return settings.RootElement.TryGetProperty("Database", out var database) && database.TryGetProperty("ThrowOnUnorderedRowLimit", out var value) ? value.GetBoolean() : null;
        }

        Assert.Equal("Database:ThrowOnUnorderedRowLimit", ApplicationDbContextOptions.ThrowOnUnorderedRowLimitSetting);
        Assert.True(SwitchIn("appsettings.Development.json"));
        Assert.Null(SwitchIn("appsettings.json"));

        Assert.Contains("AddApplicationDbContexts(connectionString, throwOnUnorderedRowLimit: true)", File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.E2ESetup", "PrepServices.cs")), StringComparison.Ordinal);
        foreach (var host in new[] { "Infrastructure/RealDatabase.cs", "Infrastructure/ServiceHost.cs", "Infrastructure/AppHost.cs", "Data/SeedingHost.cs" })
        {
            Assert.Contains("throwOnUnorderedRowLimit: true", File.ReadAllText(Path.Combine(RepoFiles.Root, "Staffora.Test", "Integration", host)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_migrations_are_one_InitialCreate_until_the_first_shared_deployment()
    {
        using var context = new ApplicationDbContext(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));

        var migration = Assert.Single(context.Database.GetMigrations());
        Assert.EndsWith("_InitialCreate", migration, StringComparison.Ordinal);
    }
}
