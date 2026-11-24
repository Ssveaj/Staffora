using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;

namespace Staffora.Core.Models;

public sealed record WorkPatternListItem(Guid Id, string Name, string? Description, int CycleWeeks, WorkPatternState State, int Segments, TimeSpan WorkedPerCycle)
{
    public int StaffToday { get; init; }

    public int StaffUpcoming { get; init; }

    public string StaffLabel => (StaffToday, StaffUpcoming) switch
    {
        (0, 0) => "None",
        (var today, 0) => $"{today} today",
        (0, var upcoming) => $"{upcoming} upcoming",
        var (today, upcoming) => $"{today} today · {upcoming} upcoming",
    };

    public string CycleLabel => CycleWeeks == 1 ? "1 week" : $"{CycleWeeks} weeks";

    public string HoursLabel => $"{WorkedPerCycle.TotalHours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} h per {(CycleWeeks == 1 ? "week" : CycleLabel)}";

    public static TimeSpan WorkedIn(IEnumerable<PatternSegment> segments) =>
        segments.Where(segment => segment.Start < segment.End).Aggregate(TimeSpan.Zero, (total, segment) => total + (segment.End - segment.Start));
}

public sealed record PatternStaffRow(Guid Id, Guid UserId, string Staff, DateOnly From, DateOnly? To, ResourcePatternAssignmentState State, DateOnly Today)
{
    public string PeriodLabel => $"{ScheduleRules.DateText(From)} {(To is { } to ? $"to {ScheduleRules.DateText(to)}" : "until further notice")}";

    public string WhenLabel => From > Today ? "Upcoming" : To is { } to && to < Today ? "Ended" : "Current";
}

public sealed record PatternStaffChoice(Guid ResourceId, string Name, string Email, StaffPatternCover? Cover, bool HasThisPattern = false)
{
    public const string HasThisPatternLabel = "already assigned";

    public string Label => $"{Name} ({(HasThisPattern ? HasThisPatternLabel : Cover?.Label ?? StaffPatternCover.NoneLabel)})";
}

public sealed record PatternStaffChoices(Guid PatternId, string Pattern, bool Published, IReadOnlyList<PatternStaffChoice> Staff, DateOnly Today);

public sealed record PatternStaffRefusal(Guid ResourceId, string Staff, string Reason)
{
    public string Label => $"{Staff}: {Reason}";
}
