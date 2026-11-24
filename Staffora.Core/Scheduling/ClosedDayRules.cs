using System.Globalization;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Core.Scheduling;

public sealed record ScheduledClosedDay(Guid ClosedDayId, string Name, DateOnly FirstDay, DateOnly LastDay, IReadOnlySet<Guid>? Units)
{
    public bool AppliesTo(IReadOnlySet<Guid> staffUnits)
    {
        ArgumentNullException.ThrowIfNull(staffUnits);
        return Units is null || staffUnits.Overlaps(Units);
    }

    public bool Covers(DateOnly day) => FirstDay <= day && day <= LastDay;
}

public sealed record ClosedTime(DateOnly Date, Guid ClosedDayId, string Name);

public sealed record ClosedDayMark(Guid ClosedDayId, string Name, string? Unit)
{
    public string Text => Unit is null ? $"Closed for {Name}" : $"Closed for {Name} in {Unit}";
}

public sealed record ClosedDayFact(Guid ClosedDayId, string Name, DateOnly FirstDay, DateOnly LastDay, string? Unit);

public static class ClosedDayRules
{
    public const int MaxDays = 366;

    public static readonly NameAndDescriptionRule TextRule = new("closed day", ClosedDay.NameMaxLength, 0);

    public const string RangeReversed = "The last day comes before the first day.";

    public static readonly string RangeTooLong = $"A closed period lasts at most {MaxDays} days.";

    public const string OutOfCalendar = "The closed day lies outside the calendar.";

    public static string? Problem(string name, DateOnly firstDay, DateOnly lastDay)
    {
        ArgumentNullException.ThrowIfNull(name);
        return TextRule.Problem(name, null)
            ?? (firstDay == DateOnly.MinValue || lastDay >= DateOnly.MaxValue ? OutOfCalendar
            : lastDay < firstDay ? RangeReversed
            : lastDay.DayNumber - firstDay.DayNumber + 1 > MaxDays ? RangeTooLong
            : null);
    }

    public static string BlockReason(string name) => $"Closed for {name}";

    public static (DateTime StartUtc, DateTime EndUtc) Instants(DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (WallClock.ToUtc(firstDay, TimeOnly.MinValue, zone), WallClock.ToUtc(lastDay.AddDays(1), TimeOnly.MinValue, zone));
    }

    public static string PeriodText(DateOnly firstDay, DateOnly lastDay) =>
        firstDay == lastDay ? LongDay(firstDay) : $"{LongDay(firstDay)} to {LongDay(lastDay)}";

    public static int DayCount(DateOnly firstDay, DateOnly lastDay) => lastDay.DayNumber - firstDay.DayNumber + 1;

    public static IReadOnlyDictionary<DateOnly, IReadOnlyList<ClosedDayMark>> MarkDays(IEnumerable<ClosedDayFact> closedDays, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(closedDays);
        var marks = new Dictionary<DateOnly, List<ClosedDayMark>>();
        foreach (var closed in closedDays.Where(closed => closed.FirstDay <= to && closed.LastDay >= from).OrderBy(closed => closed.Unit is not null).ThenBy(closed => closed.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(closed => closed.ClosedDayId))
        {
            var first = closed.FirstDay < from ? from : closed.FirstDay;
            var last = closed.LastDay > to ? to : closed.LastDay;
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                (marks.TryGetValue(day, out var shown) ? shown : marks[day] = []).Add(new ClosedDayMark(closed.ClosedDayId, closed.Name, closed.Unit));
                if (day == DateOnly.MaxValue)
                {
                    break;
                }
            }
        }

        return marks.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<ClosedDayMark>)entry.Value);
    }

    public static string MarksText(IReadOnlyList<ClosedDayMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        return string.Join(", ", marks.Select(mark => mark.Text));
    }

    private static string LongDay(DateOnly day) => day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
}
