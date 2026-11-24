using Staffora.Services.Coordination;

namespace Staffora.Test.Unit.Coordination;

public class RefreshThrottleTests
{
    private static readonly DateTimeOffset Start = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_first_change_reloads_at_once_and_a_burst_within_five_seconds_is_one_reload_when_they_have_passed()
    {
        var clock = new ManualClock(Start);
        var reloads = 0;
        using var throttle = new RefreshThrottle(clock, () => { reloads++; return Task.CompletedTask; });
        Assert.Equal(TimeSpan.FromSeconds(5), throttle.Interval);

        await throttle.SignalAsync();
        Assert.Equal(1, reloads);

        clock.Advance(TimeSpan.FromSeconds(1));
        await throttle.SignalAsync();
        await throttle.SignalAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await throttle.SignalAsync();
        Assert.Equal(1, reloads);
        Assert.True(throttle.IsWaiting);
        Assert.Equal(1, clock.PendingTimers);

        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal(1, reloads);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, reloads);
        Assert.False(throttle.IsWaiting);

        clock.Advance(TimeSpan.FromSeconds(30));
        await throttle.SignalAsync();
        Assert.Equal(3, reloads);
        Assert.Equal(3, throttle.Reloads);
    }

    [Fact]
    public async Task A_change_right_after_a_waited_reload_waits_its_own_five_seconds()
    {
        var clock = new ManualClock(Start);
        var reloads = new List<DateTimeOffset>();
        using var throttle = new RefreshThrottle(clock, () => { reloads.Add(clock.GetUtcNow()); return Task.CompletedTask; });
        await throttle.SignalAsync();
        await throttle.SignalAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await throttle.SignalAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal([Start, Start.AddSeconds(5), Start.AddSeconds(10)], reloads);
    }

    [Fact]
    public async Task Nothing_reloads_once_the_page_is_gone_and_a_waiting_reload_is_dropped()
    {
        var clock = new ManualClock(Start);
        var reloads = 0;
        var throttle = new RefreshThrottle(clock, () => { reloads++; return Task.CompletedTask; });
        await throttle.SignalAsync();
        await throttle.SignalAsync();
        Assert.True(throttle.IsWaiting);

        throttle.Dispose();
        Assert.Equal(0, clock.PendingTimers);
        clock.Advance(TimeSpan.FromMinutes(1));
        await throttle.SignalAsync();
        Assert.Equal(1, reloads);
    }
}
