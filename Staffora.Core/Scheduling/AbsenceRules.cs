using System.Globalization;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Core.Scheduling;

public sealed record ScheduledAbsence(Guid AbsenceId, Guid ResourceId, AbsenceKind Kind, ResourceAbsenceState State, DateOnly FirstDay, TimeOnly? StartTime, DateOnly LastDay, TimeOnly? EndTime);

public static class AbsenceRules
{
    public const string EndsBeforeStart = "The absence ends before it starts: its last day is before its first.";

    public const string TimesOutOfOrder = "On a single day the absence ends after it starts: the until time is later than the from time.";

    public const string EndsAtMidnight = "An absence does not end at 00:00 on its last day: end it on the day before, with no time.";

    public const string OutOfCalendar = "The absence lies outside the calendar.";

    public const string NotInServiceReason = "The staff member is inactive";

    public static readonly ResourceAbsenceState[] UndecidedStates = [ResourceAbsenceState.Requested, ResourceAbsenceState.Question, ResourceAbsenceState.StaffQuestion];

    public static readonly ResourceAbsenceState[] ScheduledStates = [.. UndecidedStates, ResourceAbsenceState.Approved];

    public static bool IsUndecided(ResourceAbsenceState state) => Array.IndexOf(UndecidedStates, state) >= 0;

    public static bool Blocks(AbsenceKind kind, ResourceAbsenceState state) =>
        state == ResourceAbsenceState.Approved || (IsUndecided(state) && kind == AbsenceKind.SickLeave);

    public static string OwnStatusText(ResourceAbsenceState state) => state switch
    {
        ResourceAbsenceState.Approved => "approved",
        ResourceAbsenceState.Question => "a question to you",
        ResourceAbsenceState.StaffQuestion => "your question to the coordinator",
        ResourceAbsenceState.Rejected => "rejected",
        ResourceAbsenceState.Canceled => "canceled",
        _ => "waiting for approval",
    };

    public const string UnknownKind = "The absence is either a planned leave or a sick leave.";

    public static bool IsKnownKind(AbsenceKind kind) => Enum.IsDefined(kind);

    public static IReadOnlyList<string> Problems(AbsenceKind kind, DateOnly firstDay, TimeOnly? startTime, DateOnly lastDay, TimeOnly? endTime) =>
        IsKnownKind(kind) ? Problems(firstDay, startTime, lastDay, endTime) : [UnknownKind, .. Problems(firstDay, startTime, lastDay, endTime)];

    public static IReadOnlyList<string> Problems(DateOnly firstDay, TimeOnly? startTime, DateOnly lastDay, TimeOnly? endTime)
    {
        var problems = new List<string>();
        if (firstDay == DateOnly.MinValue || lastDay >= DateOnly.MaxValue)
        {
            problems.Add(OutOfCalendar);
        }

        if (lastDay < firstDay)
        {
            problems.Add(EndsBeforeStart);
        }
        else if (lastDay == firstDay && startTime is { } start && endTime is { } end && end <= start)
        {
            problems.Add(TimesOutOfOrder);
        }

        if (endTime == TimeOnly.MinValue)
        {
            problems.Add(EndsAtMidnight);
        }

        return problems;
    }

    public static (DateTime StartUtc, DateTime EndUtc) Instants(DateOnly firstDay, TimeOnly? startTime, DateOnly lastDay, TimeOnly? endTime, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var start = WallClock.ToUtc(firstDay, startTime ?? TimeOnly.MinValue, zone);
        var end = endTime is { } until ? WallClock.ToUtc(lastDay, until, zone) : WallClock.ToUtc(lastDay.AddDays(1), TimeOnly.MinValue, zone);
        return (start, end);
    }

    public static string BlockReason(AbsenceKind kind, ResourceAbsenceState state) => kind switch
    {
        AbsenceKind.SickLeave => state == ResourceAbsenceState.Approved ? "On sick leave" : "On sick leave (reported)",
        _ => "On planned leave",
    };

    public static string KindText(AbsenceKind kind) => kind == AbsenceKind.SickLeave ? "sick leave" : "planned leave";

    public static string PeriodText(DateOnly firstDay, TimeOnly? startTime, DateOnly lastDay, TimeOnly? endTime)
    {
        if (firstDay == lastDay)
        {
            var day = $"on {ScheduleRules.DateText(firstDay)}";
            return (startTime, endTime) switch
            {
                ({ } from, { } until) => $"{day} from {TimeText(from)} until {TimeText(until)}",
                ({ } from, null) => $"{day} from {TimeText(from)}",
                (null, { } until) => $"{day} until {TimeText(until)}",
                _ => day,
            };
        }

        var first = startTime is { } start ? $"{ScheduleRules.DateText(firstDay)} {TimeText(start)}" : ScheduleRules.DateText(firstDay);
        var last = endTime is { } end ? $"{ScheduleRules.DateText(lastDay)} {TimeText(end)}" : ScheduleRules.DateText(lastDay);
        return $"from {first} to {last}";
    }

    public static string TimeText(TimeOnly time) => time.ToString("HH':'mm", CultureInfo.InvariantCulture);
}
