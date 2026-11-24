using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data;

public sealed class TenantHeldException : InvalidOperationException
{
    public TenantHeldException() : base(TenantStates.HeldDataReason)
    {
    }
}
