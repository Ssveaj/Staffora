namespace Staffora.Data;

public sealed class TenantIsolationException : InvalidOperationException
{
    public TenantIsolationException(string message) : base(message)
    {
    }
}
