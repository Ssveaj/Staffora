using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Authorization;

namespace Staffora.Services.Coordination;

public enum ChangeInterest
{
    Coordination = 0,

    OwnWork = 1,
}

public sealed class TenantChangeFeed
{
    private readonly TenantChangeBus bus;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;

    public TenantChangeFeed(TenantChangeBus bus, IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService)
    {
        this.bus = bus;
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
    }

    public long? Mark()
    {
        var session = this.runtime.Current;
        return session.HasPlatformReach() || session.EffectiveUserId is null || session.TenantId is not { } tenantId ? null : this.bus.Mark(tenantId);
    }

    public async Task<IDisposable?> SubscribeAsync(ChangeInterest interest, Func<Task> onChange, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var session = this.runtime.Current;
        if (session.HasPlatformReach() || session.EffectiveUserId is not { } userId || session.TenantId is not { } tenantId)
        {
            return null;
        }

        switch (interest)
        {
            case ChangeInterest.Coordination:
                return await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken)
                    ? this.bus.Subscribe(tenantId, _ => onChange())
                    : null;

            case ChangeInterest.OwnWork:
                Guid? own;
                await using (var context = this.dbFactory.CreateDbContext(this.runtime))
                {
                    own = await context.Resources.AsNoTracking()
                        .Where(resource => resource.TenantId == tenantId && resource.UserId == userId)
                        .Select(resource => (Guid?)resource.Id)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                return own is { } record
                    ? this.bus.Subscribe(tenantId, change => change.StaffIds.Contains(record) ? onChange() : Task.CompletedTask)
                    : null;

            default:
                return null;
        }
    }
}
