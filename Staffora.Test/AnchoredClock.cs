using System.Diagnostics;

namespace Staffora.Test;

internal sealed class AnchoredClock : TimeProvider
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset anchor;

    public AnchoredClock(DateTimeOffset anchor)
    {
        this.anchor = anchor;
    }

    public override DateTimeOffset GetUtcNow() => this.anchor + Stopwatch.GetElapsedTime(this.started);
}
