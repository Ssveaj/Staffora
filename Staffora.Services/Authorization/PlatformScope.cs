using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Services.Authorization;

public static class PlatformScope
{
    public static bool IsPlatformOwner(this RuntimeSnapshot session) => session.IsSuperAdmin && session.IsInPlatformTenant;

    public static bool HasPlatformReach(this RuntimeSnapshot session) => session.IsPlatformOwner() && !session.IsImpersonating;

    public static bool IsPlatformOwner(this IRuntimeExecutionContext runtime) => runtime.Current.IsPlatformOwner();

    public static bool HasPlatformReach(this IRuntimeExecutionContext runtime) => runtime.Current.HasPlatformReach();

    public static IDisposable? BeginPlatformScope(this ApplicationDbContext context, IRuntimeExecutionContext runtime, string reason) =>
        context.OpenPlatformLease(runtime.Current, reason);

    public static IDisposable? BeginPlatformScopeFor(this ApplicationDbContext context, IRuntimeExecutionContext runtime, Guid? tenantId, string reason)
    {
        var session = runtime.Current;
        return tenantId == session.TenantId ? null : context.OpenPlatformLease(session, reason);
    }

    private static IDisposable? OpenPlatformLease(this ApplicationDbContext context, RuntimeSnapshot session, string reason) =>
        session.HasPlatformReach() ? context.BeginSystemScope($"Platform console: {reason}") : null;
}
