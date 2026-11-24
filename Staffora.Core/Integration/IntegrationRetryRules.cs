namespace Staffora.Core.Integration;

public static class IntegrationRetryRules
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LongestRetry = TimeSpan.FromHours(1);

    public const int MaxAttempts = 8;

    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan NoticeGrace = TimeSpan.FromMinutes(1);

    public static TimeSpan RetryAfter(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var doublings = Math.Min(attempts - 1, 20);
        var wait = TimeSpan.FromTicks(FirstRetry.Ticks * (1L << doublings));
        return wait > LongestRetry ? LongestRetry : wait;
    }

    public static bool IsSpent(int attempts) => attempts >= MaxAttempts;
}
