namespace Staffora.Core.Scheduling;

public static class WallClock
{
    private const int MaxGapMinutes = 24 * 60;

    public static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = FirstValidAfter(local, zone);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static TimeOnly LocalTimeOf(DateTime utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return utc.Kind == DateTimeKind.Utc
            ? TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone))
            : throw new ArgumentException("An instant is converted from UTC only (ADR 0020).", nameof(utc));
    }

    private static DateTime FirstValidAfter(DateTime skipped, TimeZoneInfo zone)
    {
        var minute = new DateTime(skipped.Year, skipped.Month, skipped.Day, skipped.Hour, skipped.Minute, 0, DateTimeKind.Unspecified);
        for (var step = 1; step <= MaxGapMinutes; step++)
        {
            var candidate = minute.AddMinutes(step);
            if (!zone.IsInvalidTime(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"The time zone {zone.Id} skips more than a day after {skipped:O}.");
    }
}
