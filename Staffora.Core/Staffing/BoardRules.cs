using Staffora.Core.Scheduling;

namespace Staffora.Core.Staffing;

public sealed record BoardPair(bool Asked, StaffingVerdict Verdict, QualificationMatch Match)
{
    public bool IsFree => !Asked && !Verdict.IsBlocked;
}

public static class BoardRules
{
    public const int DaysInWeek = 7;

    public const int PageSize = 25;

    public static int PageCount(int count) => Math.Max(1, (count + PageSize - 1) / PageSize);

    public static int PageWithin(int page, int count) => Math.Clamp(page, 1, PageCount(count));

    public static IReadOnlyList<DateOnly> Days(DateOnly day)
    {
        var monday = ScheduleRules.MondayOf(day);
        return [.. Enumerable.Range(0, DaysInWeek).Select(offset => monday.AddDays(offset))];
    }

    public static bool IsToStaff(PriorityKind kind) => kind is PriorityKind.Uncovered or PriorityKind.Unanswered;

    public static BoardPair Pair(StaffAvailability staff, IReadOnlyList<OccurrenceInstant> occurrences, IEnumerable<Guid> required, IReadOnlySet<Guid> held, bool asked)
    {
        ArgumentNullException.ThrowIfNull(staff);
        ArgumentNullException.ThrowIfNull(occurrences);
        var match = StaffingRules.Match(required, held);
        if (asked)
        {
            return new BoardPair(true, StaffingVerdict.None, match);
        }

        return new BoardPair(false, occurrences.Count == 0 ? StaffingVerdict.None : StaffingRules.Judge(staff, occurrences), match);
    }

    public static IReadOnlyList<(DateOnly From, DateOnly To)> Windows(DateOnly from, DateOnly to, int maxDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDays, 1);
        var windows = new List<(DateOnly, DateOnly)>();
        for (var start = from; start <= to;)
        {
            var end = to.DayNumber - start.DayNumber + 1 > maxDays ? start.AddDays(maxDays - 1) : to;
            windows.Add((start, end));
            if (end == DateOnly.MaxValue)
            {
                break;
            }

            start = end.AddDays(1);
        }

        return windows;
    }
}
