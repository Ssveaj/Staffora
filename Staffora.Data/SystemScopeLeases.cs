using Microsoft.Extensions.Logging;

namespace Staffora.Data;

internal sealed class SystemScopeLeases
{
    private readonly HashSet<Lease> open = [];
    private readonly ILogger log;

    public SystemScopeLeases(ILogger log)
    {
        this.log = log;
    }

    public bool AnyOpen => this.open.Count > 0;

    public IDisposable Open(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var lease = new Lease(this, reason);
        this.open.Add(lease);
        Record("opened", lease);
        return lease;
    }

    private void Close(Lease lease)
    {
        if (this.open.Remove(lease))
        {
            Record("closed", lease);
        }
    }

    private void Record(string change, Lease lease) =>
        this.log.LogInformation("System scope {Change} for {Reason} (depth {Depth}).", change, lease.Reason, this.open.Count);

    private sealed class Lease : IDisposable
    {
        private readonly SystemScopeLeases register;
        private readonly string reason;

        public Lease(SystemScopeLeases register, string reason)
        {
            this.register = register;
            this.reason = reason;
        }

        public string Reason => this.reason;

        public void Dispose() => this.register.Close(this);
    }
}
