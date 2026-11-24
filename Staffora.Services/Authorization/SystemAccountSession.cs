using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;

namespace Staffora.Services.Authorization;

public static class SystemAccountSession
{
    public static IRuntimeExecutionContext Apply(IRuntimeExecutionContext runtime, Guid tenantId, string actor)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.IsInitialized)
        {
            throw new InvalidOperationException($"The {actor} acts in a service scope of its own, whose runtime holds no session yet.");
        }

        runtime.ApplySession(new RuntimeSession(SystemAccount.IdFor(tenantId), IsSuperAdmin: false, BaseTenantId: tenantId, IsInPlatformTenant: false, ImpersonatedUserId: null, ImpersonatedTenantId: null));
        return runtime;
    }
}
