namespace Staffora.Services.Coordination;

public sealed class RefreshThrottle : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider clock;
    private readonly Func<Task> reload;
    private readonly Lock gate = new();
    private DateTimeOffset? lastRun;
    private ITimer? pending;
    private bool disposed;

    public RefreshThrottle(TimeProvider clock, Func<Task> reload, TimeSpan? interval = null)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.reload = reload ?? throw new ArgumentNullException(nameof(reload));
        Interval = interval ?? DefaultInterval;
        if (Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval is positive.");
        }
    }

    public TimeSpan Interval { get; }

    public int Reloads { get; private set; }

    public bool IsWaiting
    {
        get
        {
            lock (this.gate)
            {
                return this.pending is not null;
            }
        }
    }

    public Task SignalAsync()
    {
        lock (this.gate)
        {
            if (this.disposed || this.pending is not null)
            {
                return Task.CompletedTask;
            }

            var now = this.clock.GetUtcNow();
            if (this.lastRun is { } last && now - last < Interval)
            {
                this.pending = this.clock.CreateTimer(_ => _ = RunWaitingAsync(), null, last + Interval - now, Timeout.InfiniteTimeSpan);
                return Task.CompletedTask;
            }

            this.lastRun = now;
            Reloads++;
        }

        return this.reload();
    }

    private Task RunWaitingAsync()
    {
        lock (this.gate)
        {
            this.pending?.Dispose();
            this.pending = null;
            if (this.disposed)
            {
                return Task.CompletedTask;
            }

            this.lastRun = this.clock.GetUtcNow();
            Reloads++;
        }

        return this.reload();
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.disposed = true;
            this.pending?.Dispose();
            this.pending = null;
        }
    }
}
