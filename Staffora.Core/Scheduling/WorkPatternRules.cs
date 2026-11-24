using Staffora.Core.Entities.Scheduling;

namespace Staffora.Core.Scheduling;

public readonly record struct PatternSegment(int DayIndex, TimeOnly Start, TimeOnly End);

public readonly record struct WorkingSegment(DateOnly Date, TimeOnly Start, TimeOnly End);

public static class WorkPatternRules
{
    public const int DaysPerWeek = 7;

    public const int MaxCycleWeeks = 8;

    public static readonly string CycleOutOfRange = $"The cycle is 1 to {MaxCycleWeeks} weeks long.";

    public const string AnchorNotMonday = "A pattern's cycle is anchored on a Monday, its day 0.";

    public static int CycleDays(int cycleWeeks) => cycleWeeks * DaysPerWeek;

    public static DayOfWeek WeekdayOf(int dayIndex) => (DayOfWeek)((dayIndex % DaysPerWeek + 1) % DaysPerWeek);

    public static string DayLabel(int dayIndex, int cycleWeeks) =>
        dayIndex < 0 ? $"Day {dayIndex}"
        : cycleWeeks <= 1 ? WeekdayOf(dayIndex).ToString()
        : $"Week {(dayIndex / DaysPerWeek) + 1}, {WeekdayOf(dayIndex)}";

    public static IEnumerable<PatternSegment> SegmentsOf(WorkPattern pattern) =>
        pattern.Segments.Select(segment => new PatternSegment(segment.DayIndex, segment.Start, segment.End));

    public static IReadOnlyList<string> Problems(WorkPattern pattern) => Problems(pattern.CycleWeeks, SegmentsOf(pattern));

    public static IReadOnlyList<string> Problems(int cycleWeeks, IEnumerable<PatternSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var problems = new List<string>();
        if (cycleWeeks is < 1 or > MaxCycleWeeks)
        {
            problems.Add(CycleOutOfRange);
            return problems;
        }

        var days = CycleDays(cycleWeeks);
        var all = segments.ToList();
        foreach (var segment in all.Where(segment => segment.DayIndex < 0 || segment.DayIndex >= days).OrderBy(segment => segment.DayIndex))
        {
            problems.Add($"A segment ({Times(segment)}) is on a day outside the {cycleWeeks}-week cycle.");
        }

        foreach (var segment in all.Where(segment => segment.DayIndex >= 0 && segment.DayIndex < days && segment.Start >= segment.End).OrderBy(segment => segment.DayIndex).ThenBy(segment => segment.Start))
        {
            problems.Add($"{DayLabel(segment.DayIndex, cycleWeeks)}: a segment ({Times(segment)}) must start before it ends.");
        }

        foreach (var day in all.Where(segment => segment.DayIndex >= 0 && segment.DayIndex < days && segment.Start < segment.End).GroupBy(segment => segment.DayIndex).OrderBy(day => day.Key))
        {
            var ordered = day.OrderBy(segment => segment.Start).ThenBy(segment => segment.End).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Start < ordered[i - 1].End)
                {
                    problems.Add($"{DayLabel(day.Key, cycleWeeks)}: {Times(ordered[i - 1])} and {Times(ordered[i])} overlap.");
                }
            }
        }

        return problems;
    }

    public static IEnumerable<WorkingSegment> Expand(int cycleWeeks, IEnumerable<PatternSegment> segments, DateOnly anchor, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (cycleWeeks is < 1 or > MaxCycleWeeks)
            throw new ArgumentOutOfRangeException(nameof(cycleWeeks), cycleWeeks, CycleOutOfRange);

        if (anchor.DayOfWeek != DayOfWeek.Monday)
            throw new ArgumentOutOfRangeException(nameof(anchor), anchor, AnchorNotMonday);

        var days = CycleDays(cycleWeeks);
        var byDay = segments.Where(segment => segment.DayIndex >= 0 && segment.DayIndex < days)
            .GroupBy(segment => segment.DayIndex)
            .ToDictionary(day => day.Key, day => day.OrderBy(segment => segment.Start).ThenBy(segment => segment.End).ToList());
        return ExpandDays(byDay, days, anchor, from, to);
    }

    private static IEnumerable<WorkingSegment> ExpandDays(Dictionary<int, List<PatternSegment>> byDay, int days, DateOnly anchor, DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var index = ((date.DayNumber - anchor.DayNumber) % days + days) % days;
            if (byDay.TryGetValue(index, out var worked))
            {
                foreach (var segment in worked)
                {
                    yield return new WorkingSegment(date, segment.Start, segment.End);
                }
            }

            if (date == DateOnly.MaxValue)
            {
                yield break;
            }
        }
    }

    private static string Times(PatternSegment segment) => $"{TimeText(segment.Start)}-{TimeText(segment.End)}";

    public static string TimeText(TimeOnly time) => time.ToString("HH':'mm", System.Globalization.CultureInfo.InvariantCulture);
}
