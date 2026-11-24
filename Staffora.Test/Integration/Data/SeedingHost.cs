using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Data.Seed;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

internal static class SeedingHost
{
    public static async Task<IReadOnlyList<string>> SeedAsync(RealDatabase database, TimeProvider clock, bool includeDevelopmentData, CancellationToken cancellationToken) =>
        (await SeedAnsweringAsync(database, clock, includeDevelopmentData, cancellationToken)).ContextLog;

    public static async Task<(Guid? PlatformOwnerId, IReadOnlyList<string> ContextLog)> SeedAnsweringAsync(RealDatabase database, TimeProvider clock, bool includeDevelopmentData, CancellationToken cancellationToken)
    {
        var contextLog = new ContextLog();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(contextLog).SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(clock);
        services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        services.AddApplicationDbContexts(database.ConnectionString, throwOnUnorderedRowLimit: true);
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Stores.SchemaVersion = ApplicationDbContextOptions.IdentitySchemaVersion;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddApplicationStores();
        services.AddScoped<DatabaseSeeder>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var owner = await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync(includeDevelopmentData, cancellationToken);
        return (owner, contextLog.Messages);
    }

    public static async Task<List<string>> SnapshotAsync(RealDatabase database, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        var rows = new List<string>();
        rows.AddRange(await context.Tenants.OrderBy(row => row.Id).Select(row => $"Tenant {row.Id} {row.Name} {row.State} {row.IsRootTenant} {row.Created:O} {row.CreatedById}").ToListAsync(cancellationToken));
        rows.AddRange(await context.Users.OrderBy(row => row.Id).Select(row => $"User {row.Id} {row.Email} {row.TenantId} {row.SecurityStamp} {row.ConcurrencyStamp}").ToListAsync(cancellationToken));
        rows.AddRange(await context.Roles.OrderBy(row => row.Id).Select(row => $"Role {row.Id} {row.Name} {row.ConcurrencyStamp}").ToListAsync(cancellationToken));
        rows.AddRange(await context.UserRoles.OrderBy(row => row.UserId).Select(row => $"UserRole {row.UserId} {row.RoleId}").ToListAsync(cancellationToken));
        foreach (var entityType in context.Model.GetEntityTypes().Where(type => typeof(ITenantEntity).IsAssignableFrom(type.ClrType)))
        {
            var rowsOfType = await (Task<List<string>>)typeof(SeedingHost)
                .GetMethod(nameof(TenantRowsAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(null, [context, cancellationToken])!;
            rows.AddRange(rowsOfType);
        }

        return rows;
    }

    private static async Task<List<string>> TenantRowsAsync<TEntity>(TestDataContext context, CancellationToken cancellationToken) where TEntity : class, ITenantEntity
    {
        var rows = await context.Set<TEntity>()
            .Select(row => new { row.TenantId, row.Created, row.CreatedById, row.Modified, row.ModifiedById, row.ImpersonatedById })
            .ToListAsync(cancellationToken);
        return rows.Select(row => $"{typeof(TEntity).Name} {row}").Order(StringComparer.Ordinal).ToList();
    }

    private sealed class ContextLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();

        public IReadOnlyList<string> Messages => [.. this.messages];

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(ApplicationDbContext).FullName ? new Recorder(this.messages) : NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Recorder : ILogger
        {
            private readonly ConcurrentQueue<string> messages;

            public Recorder(ConcurrentQueue<string> messages)
            {
                this.messages = messages;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                this.messages.Enqueue(formatter(state, exception));
        }
    }
}
