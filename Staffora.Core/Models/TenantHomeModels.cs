using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Core.Models;

public enum HomeTone
{
    Calm = 0,

    Attention = 1,

    Problem = 2,

    Held = 3,
}

public sealed record AdministrationHome(string? FirstName, DateTime ReadAt, DateOnly Today)
{
    public HomeStaffing? Staffing { get; init; }

    public HomePeople? People { get; init; }

    public HomeAbsence? Absence { get; init; }

    public HomeCustomers? Customers { get; init; }

    public IReadOnlyList<HomeActivity>? Activity { get; init; }
}

public sealed record HomeStaffing(int JobsThisWeek, int WeekSlotsHeld, int WeekSlotsOpen, int OpenSlots, int OpenSlotsSoon)
{
    public int WeekSlots => WeekSlotsHeld + WeekSlotsOpen;
}

public sealed record HomePeople(int StaffInService, int WithoutWorkPattern);

public sealed record HomeAbsence(IReadOnlyList<AbsenceRow> AbsentToday, int AbsentTodayCount, IReadOnlyList<AbsenceRow> Waiting, int WaitingCount);

public sealed record HomeCustomers(int Active, int WithPortal)
{
    public HomeOrders? Orders { get; init; }
}

public sealed record HomeOrders(int Waiting, int BeingStaffed);

public sealed record HomeActivity(Guid JobId, string Job, JobState From, JobState To, DateTime At, string Actor)
{
    public string ToLabel => HomeRules.StateName(To);
}

public sealed record CoordinatorHome(string? FirstName, string TenantName, DateTime ReadAt, DateOnly Today, HomeStaffing Staffing, HomeAnswers Answers, IReadOnlyList<HomeTodayJob> TodayJobs)
{
    public HomeApprovals? Approvals { get; init; }

    public HomePortal? Portal { get; init; }

    public int? WithoutWorkPattern { get; init; }
}

public sealed record HomeAnswers(int Unanswered, IReadOnlyList<HomeYes> Yeses, int YesCount);

public sealed record HomeYes(Guid AssignmentId, Guid JobId, string Job, string Slot, string Staff, string Customer, DateOnly PeriodStart);

public sealed record HomeTodayJob(Guid JobId, string Job, string Customer, JobState State, string? Place, IReadOnlyList<OccurrenceInstant> Times, int Slots, int Held)
{
    public string TimesLabel => string.Join(", ", Times.Select(time => $"{WorkPatternRules.TimeText(time.Start)}-{WorkPatternRules.TimeText(time.End)}"));

    public string HeldLabel => Slots == 0 ? "No slots" : string.Create(CultureInfo.InvariantCulture, $"{Held} of {Slots} held");
}

public sealed record HomeApprovals(IReadOnlyList<AbsenceRow> Waiting, int WaitingCount);

public sealed record HomePortal(HomeOrders Counts, IReadOnlyList<HomePortalOrder> Orders, IReadOnlyList<HomeCustomerMessage> Messages, int MessageCount);

public sealed record HomePortalOrder(Guid JobId, string Job, string Customer, JobState State, DateOnly PeriodStart)
{
    public string StateLabel => State == JobState.AwaitingCoordination ? "Waiting for a coordinator" : "Being staffed";
}

public sealed record HomeCustomerMessage(Guid JobId, string Job, string Customer, DateTime At);

public sealed record OwnTime(DateOnly Date, OwnOccurrenceEntry Entry);

public sealed record OwnDayCount(DateOnly Date, int Assigned, int Asked);

public static class HomeRules
{
    public const int Shown = 5;

    public const int SoonDays = 7;

    public static HomeTone OpenSlotsTone(HomeStaffing staffing)
    {
        ArgumentNullException.ThrowIfNull(staffing);
        return staffing.OpenSlotsSoon > 0 ? HomeTone.Attention : HomeTone.Calm;
    }

    public static HomeTone WaitingTone(int waiting) => waiting > 0 ? HomeTone.Attention : HomeTone.Calm;

    public static string SlotRingTitle(HomeStaffing staffing)
    {
        ArgumentNullException.ThrowIfNull(staffing);
        return staffing.WeekSlots == 0 ? "No slots"
            : staffing.WeekSlotsOpen == 0 ? "All held"
            : string.Create(CultureInfo.InvariantCulture, $"{staffing.WeekSlotsHeld} of {staffing.WeekSlots}");
    }

    public static string SlotRingCaption(HomeStaffing staffing)
    {
        ArgumentNullException.ThrowIfNull(staffing);
        return staffing.WeekSlots == 0 ? "on this week's jobs"
            : staffing.WeekSlotsOpen == 0 ? $"{Count(staffing.WeekSlots, "slot", "slots")} this week"
            : "slots held this week";
    }

    public static HomeTone SlotRingTone(HomeStaffing staffing)
    {
        ArgumentNullException.ThrowIfNull(staffing);
        return staffing.WeekSlotsOpen > 0 ? HomeTone.Attention : HomeTone.Calm;
    }

    public static IReadOnlyList<OwnTime> TimesOf(IEnumerable<OwnScheduleWeek> weeks)
    {
        ArgumentNullException.ThrowIfNull(weeks);
        return [.. weeks
            .SelectMany(week => week.Days)
            .DistinctBy(day => day.Date)
            .OrderBy(day => day.Date)
            .SelectMany(day => day.Occurrences.Select(entry => new OwnTime(day.Date, entry)))];
    }

    public static IReadOnlyList<OwnTime> NextUp(IReadOnlyList<OwnTime> times, DateOnly today, TimeOnly now, int count)
    {
        ArgumentNullException.ThrowIfNull(times);

        bool Continues(OwnTime time) => time.Entry.Start == TimeOnly.MinValue
            && times.Any(other => other.Entry.AssignmentId == time.Entry.AssignmentId && other.Date == time.Date.AddDays(-1) && other.Entry.End == TimeOnly.MinValue);

        return [.. times
            .Where(time => time.Entry.State == SlotAssignmentState.Assigned)
            .Where(time => time.Date > today || (time.Date == today && (time.Entry.End > now || time.Entry.End == TimeOnly.MinValue)))
            .Where(time => !Continues(time))
            .OrderBy(time => time.Date)
            .ThenBy(time => time.Entry.Start)
            .ThenBy(time => time.Entry.Job, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(time => time.Entry.AssignmentId)
            .Take(count)];
    }

    public static IReadOnlyList<OwnDayCount> DayCounts(OwnScheduleWeek week)
    {
        ArgumentNullException.ThrowIfNull(week);
        return [.. week.Days.Select(day => new OwnDayCount(
            day.Date,
            day.Occurrences.Count(entry => entry.State == SlotAssignmentState.Assigned),
            day.Occurrences.Count(entry => entry.State != SlotAssignmentState.Assigned)))];
    }

    public static string WeekRingTitle(int assigned, int toCome) =>
        assigned == 0 ? "Free week"
        : toCome == 0 ? "All done"
        : string.Create(CultureInfo.InvariantCulture, $"{toCome} to go");

    public static string WeekRingCaption(int assigned, int toCome) =>
        assigned == 0 ? "nothing assigned"
        : toCome == 0 ? $"{Count(assigned, "time", "times")} this week"
        : string.Create(CultureInfo.InvariantCulture, $"of {assigned} this week");

    public static string WhenText(DateOnly date, TimeOnly start, DateOnly today)
    {
        var time = WorkPatternRules.TimeText(start);
        return date == today ? $"Today {time}"
            : date == today.AddDays(1) ? $"Tomorrow {time}"
            : $"{DayText(date)} · {time}";
    }

    public static string DayText(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    public static string WeekdayText(DateOnly date) => date.ToString("ddd", CultureInfo.InvariantCulture);

    public static string StateName(JobState state) =>
        typeof(JobState).GetField(state.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? state.ToString();

    public static string Count(int count, string singular, string plural) => PlatformOverviewRules.Count(count, singular, plural);
}
