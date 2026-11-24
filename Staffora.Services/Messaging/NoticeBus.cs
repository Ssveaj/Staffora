using Microsoft.Extensions.Logging;

namespace Staffora.Services.Messaging;

public sealed class NoticeBus
{
    private readonly Lock gate = new();
    private readonly Dictionary<(Guid TenantId, Guid UserId), List<Subscription>> subscriptions = [];
    private readonly ILogger<NoticeBus> log;

    public NoticeBus(ILogger<NoticeBus> log)
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

    public IDisposable Subscribe(Guid tenantId, Guid userId, Func<NoticeArrived, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, (tenantId, userId), handler);
        lock (this.gate)
        {
            if (!this.subscriptions.TryGetValue(subscription.Key, out var list))
            {
                this.subscriptions[subscription.Key] = list = [];
            }

            list.Add(subscription);
        }

        return subscription;
    }

    public void Publish(Guid tenantId, Guid userId, NoticeArrived notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Subscription[] listeners;
        lock (this.gate)
        {
            listeners = this.subscriptions.TryGetValue((tenantId, userId), out var list) ? [.. list] : [];
        }

        foreach (var listener in listeners)
        {
            _ = NotifyAsync(listener, notice);
        }
    }

    private async Task NotifyAsync(Subscription listener, NoticeArrived notice)
    {
        try
        {
            await listener.Handler(notice);
        }
        catch (Exception exception)
        {
            this.log.LogWarning(exception, "A circuit failed to take notice {StoredMessageId}; its bell reads the count again when it next opens.", notice.StoredMessageId);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (this.gate)
        {
            if (this.subscriptions.TryGetValue(subscription.Key, out var list) && list.Remove(subscription) && list.Count == 0)
            {
                this.subscriptions.Remove(subscription.Key);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly NoticeBus bus;

        public Subscription(NoticeBus bus, (Guid TenantId, Guid UserId) key, Func<NoticeArrived, Task> handler)
        {
            this.bus = bus;
            Key = key;
            Handler = handler;
        }

        public (Guid TenantId, Guid UserId) Key { get; }

        public Func<NoticeArrived, Task> Handler { get; }

        public void Dispose() => this.bus.Remove(this);
    }
}

public sealed record NoticeArrived(Guid StoredMessageId, bool MarkedRead = false)
{
    public static NoticeArrived Read { get; } = new(Guid.Empty, MarkedRead: true);
}
