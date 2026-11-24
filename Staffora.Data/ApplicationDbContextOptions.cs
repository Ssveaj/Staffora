using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Staffora.Data;

public static class ApplicationDbContextOptions
{
    public const string ThrowOnUnorderedRowLimitSetting = "Database:ThrowOnUnorderedRowLimit";

    public static readonly Version IdentitySchemaVersion = IdentitySchemaVersions.Version2;

    private static readonly IServiceProvider servicesOutsideTheApp = new ServiceCollection()
        .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersion)
        .BuildServiceProvider();

    public static DbContextOptionsBuilder UseApplicationSqlServer(this DbContextOptionsBuilder options, string connectionString, bool throwOnUnorderedRowLimit = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.UseSqlServer(connectionString).AddInterceptors(BulkUpdateGuard.Instance, TenantHoldInterceptor.Instance, DatabaseCommandLog.Instance);

        options.ConfigureWarnings(warnings => warnings.Log((SqlServerEventId.SavepointsDisabledBecauseOfMARS, LogLevel.Debug)));
        if (throwOnUnorderedRowLimit)
        {
            options.ConfigureWarnings(warnings => warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning));
        }

        return options;
    }

    public static DbContextOptions<ApplicationDbContext> Create(string connectionString, bool throwOnUnorderedRowLimit = false)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
        builder.UseApplicationSqlServer(connectionString, throwOnUnorderedRowLimit);
        builder.UseApplicationServiceProvider(servicesOutsideTheApp);
        return builder.Options;
    }
}
