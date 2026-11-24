using Microsoft.Extensions.Diagnostics.HealthChecks;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Blazor.Helper;

public sealed class DatabaseHealthCheck : IHealthCheck
{
    public const string Name = "database";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> contexts;
    private readonly IRuntimeExecutionContext runtime;

    public DatabaseHealthCheck(IApplicationDbContextFactory<ApplicationDbContext> contexts, IRuntimeExecutionContext runtime)
    {
        this.contexts = contexts;
        this.runtime = runtime;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var database = this.contexts.CreateDbContext(this.runtime);
            return await database.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database does not accept connections.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("The database could not be reached.", ex);
        }
    }
}
