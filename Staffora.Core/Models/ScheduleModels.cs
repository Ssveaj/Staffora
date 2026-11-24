using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;

namespace Staffora.Core.Models;

public sealed record PatternAssignmentRow(Guid Id, string Pattern, WorkPatternState PatternState, DateOnly From, DateOnly? To, ResourcePatternAssignmentState State)
{
    public string PatternLabel => PatternState == WorkPatternState.Archived ? $"{Pattern} (archived pattern)" : Pattern;

    public string PeriodLabel => $"{ScheduleRules.DateText(From)} {(To is { } to ? $"to {ScheduleRules.DateText(to)}" : "until further notice")}";
}

public sealed record ScheduleDay(DateOnly Date, IReadOnlyList<WorkingTime> Working)
{
    public string HoursLabel => string.Join(", ", Working.Select(segment => $"{WorkPatternRules.TimeText(segment.LocalStart)}–{WorkPatternRules.TimeText(segment.LocalEnd)}"));
}

public sealed record ScheduleWeek(DateOnly Monday, IReadOnlyList<ScheduleDay> Days)
{
    public TimeSpan Worked => Days.SelectMany(day => day.Working).Aggregate(TimeSpan.Zero, (total, segment) => total + segment.Duration);

    public string WorkedLabel => $"{Worked.TotalHours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} h";
}

public sealed record StaffSchedule(Guid ResourceId, string TimeZoneId, IReadOnlyList<ScheduleWeek> Weeks)
{
    public const int PeriodWeeks = 20;

    public static StaffSchedule ByWeek(StaffAvailability availability, DateOnly firstMonday, int weeks, string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(availability);
        if (firstMonday.DayOfWeek != DayOfWeek.Monday)
            throw new ArgumentOutOfRangeException(nameof(firstMonday), firstMonday, "A schedule's weeks start on a Monday.");

        var byDate = availability.Working.ToLookup(segment => segment.Date);
        List<ScheduleWeek> rows = [.. Enumerable.Range(0, Math.Max(weeks, 0)).Select(week =>
        {
            var monday = firstMonday.AddDays(week * WorkPatternRules.DaysPerWeek);
            return new ScheduleWeek(monday, [.. Enumerable.Range(0, WorkPatternRules.DaysPerWeek).Select(day => monday.AddDays(day)).Select(date => new ScheduleDay(date, [.. byDate[date].OrderBy(segment => segment.StartUtc)]))]);
        })];
        return new StaffSchedule(availability.ResourceId, timeZoneId, rows);
    }

    public static DateOnly LastDayOf(DateOnly firstMonday, int weeks) => firstMonday.AddDays((Math.Max(weeks, 0) * WorkPatternRules.DaysPerWeek) - 1);
}

public sealed record OwnScheduleWeek(DateOnly Monday, DateOnly Today, string TimeZoneId, IReadOnlyList<OwnScheduleDay> Days)
{
    public DateOnly Sunday => Monday.AddDays(WorkPatternRules.DaysPerWeek - 1);

    public string Label => $"{ScheduleRules.DateText(Monday)} to {ScheduleRules.DateText(Sunday)}";

    public bool IsCurrent => Monday <= Today && Today <= Sunday;

    public TimeSpan Worked => Days.SelectMany(day => day.Working).Aggregate(TimeSpan.Zero, (total, segment) => total + segment.Duration);

    public string WorkedLabel => $"{Worked.TotalHours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} h";
}

public sealed record OwnScheduleDay(DateOnly Date, IReadOnlyList<WorkingTime> Working, IReadOnlyList<OwnAbsenceEntry> Absences, IReadOnlyList<OwnOccurrenceEntry> Occurrences)
{
    public string Label => $"{Date.DayOfWeek} {ScheduleRules.DateText(Date)}";

    public string HoursLabel => Working.Count == 0
        ? "No working hours"
        : string.Join(", ", Working.Select(segment => $"{WorkPatternRules.TimeText(segment.LocalStart)}–{WorkPatternRules.TimeText(segment.LocalEnd)}"));

    public bool IsQuiet => Absences.Count == 0 && Occurrences.Count == 0;

    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<string> Holidays { get; init; } = [];
}

public sealed record OwnAbsenceEntry(Guid AbsenceId, AbsenceKind Kind, ResourceAbsenceState State, TimeOnly? From, TimeOnly? Until)
{
    public string Label => $"{(Kind == AbsenceKind.SickLeave ? "Sick leave" : "Planned leave")}, {State switch
    {
        ResourceAbsenceState.Requested when Kind == AbsenceKind.SickLeave => "reported",
        _ => AbsenceRules.OwnStatusText(State),
    }}";

    public string TimesLabel => (From, Until) switch
    {
        ({ } from, { } until) => $"{WorkPatternRules.TimeText(from)}–{WorkPatternRules.TimeText(until)}",
        ({ } from, null) => $"From {WorkPatternRules.TimeText(from)}",
        (null, { } until) => $"Until {WorkPatternRules.TimeText(until)}",
        _ => "All day",
    };

    public bool Blocks => AbsenceRules.Blocks(Kind, State);
}

public sealed record OwnOccurrenceEntry(Guid AssignmentId, string Job, string Slot, SlotAssignmentState State, TimeOnly Start, TimeOnly End)
{
    public string? Place { get; init; }

    public string? Customer { get; init; }

    public string TimesLabel => $"{WorkPatternRules.TimeText(Start)}–{WorkPatternRules.TimeText(End)}";

    public string StateLabel => State switch
    {
        SlotAssignmentState.Assigned => "You are assigned",
        SlotAssignmentState.Accepted => "You said yes",
        _ => "You are asked",
    };
}
