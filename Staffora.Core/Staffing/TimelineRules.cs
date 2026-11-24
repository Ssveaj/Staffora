namespace Staffora.Core.Staffing;

public sealed record TimelineSpan(double From, double Length)
{
    public double To => From + Length;
}

public sealed record TimelineNow(double At, string Text);

public static class TimelineRules
{
    public const int HoursInDay = 24;

    public const int UsualStartHour = 7;

    public static IReadOnlyList<int> HourMarks { get; } = [.. Enumerable.Range(0, HoursInDay + 1)];

    public static TimelineSpan Place(TimeOnly start, TimeOnly end)
    {
        var from = Minute(start);
        var until = EndMinute(end);
        return new TimelineSpan(Math.Round(from / 60d, 4), Math.Round(Math.Max(0, until - from) / 60d, 4));
    }

    public static double Pixels(double hours, int hourWidth) => Math.Round(hours * hourWidth, 2);

    public static TimelineNow? NowOn(DateTimeOffset localNow, DateOnly shownDay)
    {
        if (DateOnly.FromDateTime(localNow.DateTime) != shownDay)
        {
            return null;
        }

        var minute = (localNow.Hour * 60) + localNow.Minute;
        return new TimelineNow(Math.Round(minute / 60d, 4), $"{localNow.Hour:00}:{localNow.Minute:00}");
    }

    public static int OpeningHour(IEnumerable<TimeOnly> starts)
    {
        ArgumentNullException.ThrowIfNull(starts);
        var earliest = starts.Select(start => start.Hour).DefaultIfEmpty(UsualStartHour).Min();
        return Math.Min(UsualStartHour, earliest);
    }

    public static IReadOnlyList<int> Lanes(IReadOnlyList<TimelineSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var lanes = new int[spans.Count];
        var ends = new List<double>();
        foreach (var index in Enumerable.Range(0, spans.Count).OrderBy(index => spans[index].From).ThenBy(index => index))
        {
            var span = spans[index];
            var lane = ends.FindIndex(end => end <= span.From);
            if (lane < 0)
            {
                ends.Add(span.To);
                lane = ends.Count - 1;
            }
            else
            {
                ends[lane] = span.To;
            }

            lanes[index] = lane;
        }

        return lanes;
    }

    private static int Minute(TimeOnly time) => (time.Hour * 60) + time.Minute;

    private static int EndMinute(TimeOnly time) => time == TimeOnly.MinValue ? 24 * 60 : Minute(time);
}
