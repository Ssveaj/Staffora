namespace Staffora.Core.Execution;

public class RuntimeExecutionContext : IRuntimeExecutionContext
{
    private volatile RuntimeSnapshot state = RuntimeSnapshot.Empty;

    public RuntimeExecutionContext() { }

    public RuntimeExecutionContext(Guid tenantId, Guid userId)
    {
        this.state = RuntimeSnapshot.Empty with { BaseTenantId = tenantId, UserId = userId, IsInitialized = true };
    }

    public RuntimeSnapshot Current => this.state;

    public Guid? TenantId => this.state.TenantId;
    public Guid? BaseTenantId => this.state.BaseTenantId;
    public bool IsInPlatformTenant => this.state.IsInPlatformTenant;
    public Guid? ImpersonatedTenantId => this.state.ImpersonatedTenantId;
    public Guid? UserId => this.state.UserId;
    public Guid? ImpersonatedUserId => this.state.ImpersonatedUserId;
    public bool IsImpersonating => this.state.IsImpersonating;
    public bool IsInitialized => this.state.IsInitialized;
    public bool IsSuperAdmin => this.state.IsSuperAdmin;
    public Guid? EffectiveUserId => this.state.EffectiveUserId;
    public Guid? ImpersonatedById => this.state.ImpersonatedById;

    public void SetBaseTenant(Guid? tenantId, bool isPlatformTenant = false) =>
        this.state = this.state with { BaseTenantId = tenantId, IsInPlatformTenant = tenantId is not null && isPlatformTenant };

    public void SetUser(Guid? userId, bool isSuperAdmin = false) =>
        this.state = this.state with { UserId = userId, IsSuperAdmin = isSuperAdmin };

    public void StartImpersonation(Guid userId, Guid? tenantId) =>
        this.state = this.state with { ImpersonatedUserId = userId, ImpersonatedTenantId = tenantId };

    public void EndImpersonation() =>
        this.state = this.state with { ImpersonatedUserId = null, ImpersonatedTenantId = null };

    public void ApplySession(RuntimeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.state = new RuntimeSnapshot
        {
            UserId = session.UserId,
            IsSuperAdmin = session.IsSuperAdmin,
            BaseTenantId = session.BaseTenantId,
            IsInPlatformTenant = session.BaseTenantId is not null && session.IsInPlatformTenant,
            ImpersonatedUserId = session.ImpersonatedUserId,
            ImpersonatedTenantId = session.ImpersonatedUserId is null ? null : session.ImpersonatedTenantId,
            IsInitialized = true,
        };
    }

    public void MarkInitialized() => this.state = this.state with { IsInitialized = true };

    public void Reset() => this.state = RuntimeSnapshot.Empty;
}
