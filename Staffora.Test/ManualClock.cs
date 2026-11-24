namespace Staffora.Test;

internal sealed class ManualClock : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];

    public ManualClock(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; private set; }

    public int PendingTimers
    {
        get
        {
            lock (this.gate)
            {
                return this.timers.Count(timer => timer.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (this.gate)
        {
            return Now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (this.gate)
        {
            this.timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (this.gate)
        {
            target = Now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (this.gate)
            {
                next = this.timers.Where(timer => timer.Due is { } due && due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null)
                {
                    Now = target;
                    return;
                }

                Now = next.Due!.Value;
                next.Due = next.Period is { } period && period > TimeSpan.Zero ? Now + period : null;
            }

            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (this.gate)
        {
            this.timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualClock clock;
        private readonly TimerCallback callback;
        private readonly object? state;

        public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
        {
            this.clock = clock;
            this.callback = callback;
            this.state = state;
        }

        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (this.clock.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : this.clock.Now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Fire() => this.callback(this.state);

        public void Dispose() => this.clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
