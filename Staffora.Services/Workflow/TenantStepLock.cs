using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities;
using Staffora.Core.Execution;
using Staffora.Core.Workflow;
using Staffora.Data;

namespace Staffora.Services.Workflow;

public sealed class TenantStepLock<TEntity> : IStepLock
    where TEntity : class, ITenantEntity, IStateEntity
{
    private readonly IRuntimeExecutionContext runtime;
    private readonly IReadOnlyList<AppLock> locks;

    public TenantStepLock(IRuntimeExecutionContext runtime, IReadOnlyList<AppLock> locks)
    {
        this.runtime = runtime;
        this.locks = locks;
    }

    public Type EntityType => typeof(TEntity);

    public async Task TakeAsync(ApplicationDbContext context, Guid? entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenantId = entityId is { } id
            ? await context.Set<TEntity>().AsNoTracking().Where(row => row.Id == id).Select(row => (Guid?)row.TenantId).FirstOrDefaultAsync(cancellationToken)
            : this.runtime.TenantId;
        if (tenantId is { } tenant)
        {
            await AppLock.TakeInOrderAsync(context, tenant, this.locks, cancellationToken);
        }
    }
}

public static class TenantStepLockRegistration
{
    public static IServiceCollection AddStepLock<TEntity>(this IServiceCollection services, params AppLock[] locks)
        where TEntity : class, ITenantEntity, IStateEntity =>
        services.AddScoped<IStepLock>(provider => new TenantStepLock<TEntity>(provider.GetRequiredService<IRuntimeExecutionContext>(), locks));
}
