using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Tenancy;

public sealed class TenantCreateStepLock : IStepLock
{
    public Type EntityType => typeof(Tenant);

    public async Task TakeAsync(ApplicationDbContext context, Guid? entityId, CancellationToken cancellationToken)
    {
        if (entityId is null)
        {
            await AppLock.TenantCreate.TakeForPlatformAsync(context, cancellationToken);
        }
    }
}
