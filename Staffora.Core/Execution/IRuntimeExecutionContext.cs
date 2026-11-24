namespace Staffora.Core.Execution;

public interface IRuntimeExecutionContext
{
    const string NoTenant = "No tenant is selected.";

    RuntimeSnapshot Current { get; }

    Guid? TenantId { get; }

    Guid? BaseTenantId { get; }

    bool IsInPlatformTenant { get; }

    Guid? ImpersonatedTenantId { get; }

    Guid? UserId { get; }

    Guid? ImpersonatedUserId { get; }

    bool IsImpersonating { get; }
    bool IsInitialized { get; }

    bool IsSuperAdmin { get; }

    Guid? EffectiveUserId { get; }

    Guid? ImpersonatedById { get; }

    void SetBaseTenant(Guid? tenantId, bool isPlatformTenant = false);

    void SetUser(Guid? userId, bool isSuperAdmin = false);

    void StartImpersonation(Guid userId, Guid? tenantId);

    void EndImpersonation();

    void ApplySession(RuntimeSession session);

    void MarkInitialized();
    void Reset();
}

public sealed record RuntimeSession(Guid UserId, bool IsSuperAdmin, Guid? BaseTenantId, bool IsInPlatformTenant, Guid? ImpersonatedUserId, Guid? ImpersonatedTenantId);
