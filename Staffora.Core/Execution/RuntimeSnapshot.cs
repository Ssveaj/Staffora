namespace Staffora.Core.Execution;

public sealed record RuntimeSnapshot
{
    public static readonly RuntimeSnapshot Empty = new();

    public Guid? BaseTenantId { get; internal init; }

    public bool IsInPlatformTenant { get; internal init; }

    public Guid? ImpersonatedTenantId { get; internal init; }

    public Guid? UserId { get; internal init; }

    public Guid? ImpersonatedUserId { get; internal init; }

    public bool IsSuperAdmin { get; internal init; }

    public bool IsInitialized { get; internal init; }

    public bool IsImpersonating => ImpersonatedUserId.HasValue;

    public Guid? TenantId => IsImpersonating ? ImpersonatedTenantId : BaseTenantId;

    public Guid? EffectiveUserId => ImpersonatedUserId ?? UserId;

    public Guid? ImpersonatedById => IsImpersonating ? UserId : null;
}
