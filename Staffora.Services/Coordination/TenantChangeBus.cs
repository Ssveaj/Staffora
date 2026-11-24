using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Staffora.Services.Coordination;

public sealed record TenantChange(Guid TenantId, IReadOnlySet<Guid> JobIds, IReadOnlySet<Guid> StaffIds)
{
    public bool IsEmpty => JobIds.Count == 0 && StaffIds.Count == 0;
}

public sealed class TenantChangeBus
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, List<Subscription>> subscriptions = [];
    private readonly Dictionary<Guid, long> published = [];
    private readonly ConcurrentDictionary<Task, byte> delivering = new();
    private readonly ILogger<TenantChangeBus> log;

    public TenantChangeBus(ILogger<TenantChangeBus> log)
    {
        this.log = log;
    }

    public int SubscriptionCount
    {
        get
        {
            lock (this.gate)
            {
                return this.subscriptions.Values.Sum(list => list.Count);
            }
        }
    }

    internal long Mark(Guid tenantId)
    {
        lock (this.gate)
        {
            return this.published.GetValueOrDefault(tenantId);
        }
    }

    internal Task DeliveredAsync() => Task.WhenAll(this.delivering.Keys);

    internal IDisposable Subscribe(Guid tenantId, Func<TenantChange, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A subscription names its tenant.", nameof(tenantId));
        }

        var subscription = new Subscription(this, tenantId, handler);
        lock (this.gate)
        {
            if (!this.subscriptions.TryGetValue(tenantId, out var list))
            {
                this.subscriptions[tenantId] = list = [];
            }

            list.Add(subscription);
        }

        return subscription;
    }

    public void Publish(TenantChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.IsEmpty || change.TenantId == Guid.Empty)
        {
            return;
        }

        Subscription[] listeners;
        lock (this.gate)
        {
            this.published[change.TenantId] = this.published.GetValueOrDefault(change.TenantId) + 1;
            listeners = this.subscriptions.TryGetValue(change.TenantId, out var list) ? [.. list] : [];
        }

        foreach (var listener in listeners)
        {
            Task delivery;
            using (ExecutionContext.SuppressFlow())
            {
                delivery = Task.Run(() => NotifyAsync(listener, change));
            }

            this.delivering.TryAdd(delivery, 0);
            _ = delivery.ContinueWith(static (done, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(done, out _), this.delivering, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task NotifyAsync(Subscription listener, TenantChange change)
    {
        try
        {
            if (!listener.IsDisposed)
            {
                await listener.Handler(change);
            }
        }
        catch (Exception exception)
        {
            this.log.LogWarning(exception, "A page of tenant {TenantId} failed to take a change; it reads again when it next loads.", change.TenantId);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (this.gate)
        {
            if (this.subscriptions.TryGetValue(subscription.TenantId, out var list) && list.Remove(subscription) && list.Count == 0)
            {
                this.subscriptions.Remove(subscription.TenantId);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private int disposed;
        private readonly TenantChangeBus bus;

        public Subscription(TenantChangeBus bus, Guid tenantId, Func<TenantChange, Task> handler)
        {
            this.bus = bus;
            TenantId = tenantId;
            Handler = handler;
        }

        public Guid TenantId { get; }

        public Func<TenantChange, Task> Handler { get; }

        public bool IsDisposed => Volatile.Read(ref this.disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                this.bus.Remove(this);
            }
        }
    }
}
