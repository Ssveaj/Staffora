using System.Globalization;

namespace Staffora.Core.Scheduling;

public readonly record struct ScheduledDates(Guid AssignmentId, DateOnly From, DateOnly? To, string? Label = null)
{
    public bool Covers(DateOnly date) => date >= From && (To is not { } to || date <= to);
}

public static class ScheduleRules
{
    public const string EndsBeforeStart = "The assignment ends before it starts.";

    public const string AnchorNotMonday = WorkPatternRules.AnchorNotMonday;

    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % WorkPatternRules.DaysPerWeek));

    public static bool HasReadableWeek(DateOnly date) => date > DateOnly.MinValue.AddDays(7) && date < DateOnly.MaxValue.AddDays(-7);

    public static DateOnly? DayAfter(ScheduledDates ended) => ended.To is { } to && to < DateOnly.MaxValue ? to.AddDays(1) : null;

    public static IReadOnlyList<string> Problems(DateOnly from, DateOnly? to, DateOnly anchor)
    {
        var problems = new List<string>();
        if (to is { } last && last < from)
        {
            problems.Add(EndsBeforeStart);
        }

        if (anchor.DayOfWeek != DayOfWeek.Monday)
        {
            problems.Add(AnchorNotMonday);
        }

        return problems;
    }

    public static bool Overlaps(ScheduledDates first, ScheduledDates second) =>
        first.From <= (second.To ?? DateOnly.MaxValue) && second.From <= (first.To ?? DateOnly.MaxValue);

    public static IReadOnlyList<ScheduledDates> OverlapsOf(ScheduledDates candidate, IEnumerable<ScheduledDates> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        return [.. others.Where(other => other.AssignmentId != candidate.AssignmentId && Overlaps(candidate, other)).OrderBy(other => other.From).ThenBy(other => other.AssignmentId)];
    }

    public static IReadOnlyList<string> OverlapReasons(ScheduledDates candidate, IEnumerable<ScheduledDates> others) =>
        [.. OverlapsOf(candidate, others).Select(other =>
            $"It overlaps the active assignment {Describe(other)}: a staff member works by one pattern on any date. End that assignment before this one starts, or start this one after it ends.")];

    public static ScheduledDates? AssignmentOn(DateOnly date, IEnumerable<ScheduledDates> active)
    {
        ArgumentNullException.ThrowIfNull(active);
        ScheduledDates? found = null;
        foreach (var assignment in active)
        {
            if (!assignment.Covers(date))
            {
                continue;
            }

            if (found is not { } best || assignment.From > best.From || (assignment.From == best.From && assignment.AssignmentId.CompareTo(best.AssignmentId) < 0))
            {
                found = assignment;
            }
        }

        return found;
    }

    public static string Describe(ScheduledDates dates)
    {
        var label = string.IsNullOrWhiteSpace(dates.Label) ? string.Empty : $"of {dates.Label} ";
        return $"{label}from {DateText(dates.From)} {(dates.To is { } to ? $"to {DateText(to)}" : "until further notice")}";
    }

    public static string DateText(DateOnly date) => date.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
}
