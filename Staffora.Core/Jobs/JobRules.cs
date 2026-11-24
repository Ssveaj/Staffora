using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Core.Jobs;

public readonly record struct OccurrenceTimes(Weekday Weekday, TimeOnly Start, TimeOnly End);

public readonly record struct JobOccurrence(DateOnly Date, TimeOnly Start, TimeOnly End);

public static class JobRules
{
    public const int MaxPeriodDays = 366;

    public const int MaxOccurrences = 50;

    public static readonly string TooManyOccurrences = $"A job has at most {MaxOccurrences} weekly times.";

    public const string PeriodStartMissing = "The period needs a first day.";

    public const string PeriodEndMissing = "The period needs a last day.";

    public const string PeriodEndsBeforeStart = "The period's last day is not before its first.";

    public static readonly string PeriodTooLong = $"A period lasts at most {MaxPeriodDays} days.";

    public const string PeriodValidReason = "The period has a first and a last day, the first not after the last, and lasts at most a year";

    public static IReadOnlyList<JobState> SlotsOpenStates { get; } = [JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating];

    public static IReadOnlyList<JobState> StaffingOpenStates { get; } = [JobState.Coordinating, JobState.Confirmed, JobState.InProgress];

    public static bool SlotsOpen(JobState state) => SlotsOpenStates.Contains(state);

    public static bool StaffingOpen(JobState state) => StaffingOpenStates.Contains(state);

    public static DateOnly LocalDay(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    public static IEnumerable<OccurrenceTimes> OccurrencesOf(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.TimeSlots.Select(slot => new OccurrenceTimes(slot.Weekday, slot.Start, slot.End));
    }

    public static IReadOnlyList<string> Problems(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ScheduleProblems(JobTimes.Of(job));
    }

    public static IReadOnlyList<string> PeriodProblems(DateOnly start, DateOnly end)
    {
        var problems = new List<string>();
        if (start == default)
        {
            problems.Add(PeriodStartMissing);
        }

        if (end == default)
        {
            problems.Add(PeriodEndMissing);
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        if (end < start)
        {
            problems.Add(PeriodEndsBeforeStart);
        }
        else if (end.DayNumber - start.DayNumber + 1 > MaxPeriodDays)
        {
            problems.Add(PeriodTooLong);
        }

        return problems;
    }

    public static IReadOnlyList<string> TimetableProblems(IEnumerable<OccurrenceTimes> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        var all = occurrences.ToList();
        var problems = new List<string>();

        if (all.Count > MaxOccurrences)
        {
            problems.Add(TooManyOccurrences);
            return problems;
        }

        foreach (var occurrence in all.Where(occurrence => !occurrence.Weekday.IsDefined()))
        {
            problems.Add($"A time ({Times(occurrence)}) is on no day of the week.");
        }

        foreach (var occurrence in all.Where(occurrence => occurrence.Weekday.IsDefined() && occurrence.Start >= occurrence.End).OrderBy(occurrence => occurrence.Weekday).ThenBy(occurrence => occurrence.Start))
        {
            problems.Add($"{occurrence.Weekday}: a time ({Times(occurrence)}) must start before it ends.");
        }

        foreach (var day in all.Where(occurrence => occurrence.Weekday.IsDefined() && occurrence.Start < occurrence.End).GroupBy(occurrence => occurrence.Weekday).OrderBy(day => day.Key))
        {
            var ordered = day.OrderBy(occurrence => occurrence.Start).ThenBy(occurrence => occurrence.End).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Start < ordered[i - 1].End)
                {
                    problems.Add($"{day.Key}: {Times(ordered[i - 1])} and {Times(ordered[i])} overlap.");
                }
            }
        }

        return problems;
    }

    public static IEnumerable<JobOccurrence> Occurrences(IEnumerable<OccurrenceTimes> occurrences, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        var byDay = occurrences.Where(occurrence => occurrence.Weekday.IsDefined())
            .GroupBy(occurrence => occurrence.Weekday)
            .ToDictionary(day => day.Key, day => day.OrderBy(occurrence => occurrence.Start).ThenBy(occurrence => occurrence.End).ToList());
        return Expand(byDay, from, to);
    }

    private static IEnumerable<JobOccurrence> Expand(Dictionary<Weekday, List<OccurrenceTimes>> byDay, DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (byDay.TryGetValue(WeekdayExtensions.WeekdayOf(date), out var occurrences))
            {
                foreach (var occurrence in occurrences)
                {
                    yield return new JobOccurrence(date, occurrence.Start, occurrence.End);
                }
            }

            if (date == DateOnly.MaxValue)
            {
                yield break;
            }
        }
    }

    public static string Times(OccurrenceTimes occurrence) => $"{WorkPatternRules.TimeText(occurrence.Start)}-{WorkPatternRules.TimeText(occurrence.End)}";

    public static string PeriodText(DateOnly start, DateOnly end) => $"{ScheduleRules.DateText(start)} to {ScheduleRules.DateText(end)}";

    public const string ScheduleMissing = "Choose whether the job happens once or every week.";

    public const string SingleDayMissing = "A single job needs its day.";

    public const string SingleStartMissing = "A single job needs a start time.";

    public const string SingleEndMissing = "A single job needs an end time.";

    public const string SingleTimesEqual = "A single job starts and ends at different times.";

    public const string SingleHasWeeklyTimes = "A single job has no weekly times.";

    public const string SingleLastDayWrong = "A single job's last day follows from its day and times.";

    public const string RecurringHasTimes = "A recurring job's times are its weekly times.";

    public const string ScheduleValidReason = "The job happens once, on its day from its start to its end, or every week over a valid period at valid weekly times";

    public static DateOnly SingleLastDay(DateOnly day, TimeOnly start, TimeOnly end) => end <= start ? day.AddDays(1) : day;

    public static void Normalize(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Schedule == JobSchedule.Single && job.PeriodStart != default && job.StartTime is { } start && job.EndTime is { } end)
        {
            job.PeriodEnd = SingleLastDay(job.PeriodStart, start, end);
        }
    }

    public static void Normalize(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Schedule == JobSchedule.Single && order.PeriodStart != default && order.StartTime is { } start && order.EndTime is { } end)
        {
            order.PeriodEnd = SingleLastDay(order.PeriodStart, start, end);
        }
    }

    public static IReadOnlyList<string> ScheduleProblems(JobTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        switch (times.Schedule)
        {
            case JobSchedule.Single:
                var problems = new List<string>();
                if (times.PeriodStart == default)
                {
                    problems.Add(SingleDayMissing);
                }

                if (times.StartTime is null)
                {
                    problems.Add(SingleStartMissing);
                }

                if (times.EndTime is null)
                {
                    problems.Add(SingleEndMissing);
                }

                if (times is { StartTime: { } start, EndTime: { } end })
                {
                    if (start == end)
                    {
                        problems.Add(SingleTimesEqual);
                    }
                    else if (times.PeriodStart != default && times.PeriodEnd != SingleLastDay(times.PeriodStart, start, end))
                    {
                        problems.Add(SingleLastDayWrong);
                    }
                }

                if (times.Weekly.Count > 0)
                {
                    problems.Add(SingleHasWeeklyTimes);
                }

                return problems;
            case JobSchedule.Recurring:
                return
                [
                    .. PeriodProblems(times.PeriodStart, times.PeriodEnd),
                    .. TimetableProblems(times.Weekly),
                    .. times.StartTime is not null || times.EndTime is not null ? [RecurringHasTimes] : Array.Empty<string>(),
                ];
            default:
                return [ScheduleMissing];
        }
    }

    public static bool HasTime(JobTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        return times.IsSingle ? times.StartTime is not null && times.EndTime is not null : times.Weekly.Count > 0;
    }

    public static IReadOnlyList<OccurrenceInstant> Occurrences(JobTimes times, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(zone);
        var found = new List<OccurrenceInstant>();
        if (to < from)
        {
            return found;
        }

        if (times.IsSingle)
        {
            if (times is not { StartTime: { } start, EndTime: { } end } || times.PeriodStart == default)
            {
                return found;
            }

            var startUtc = WallClock.ToUtc(times.PeriodStart, start, zone);
            var endUtc = WallClock.ToUtc(SingleLastDay(times.PeriodStart, start, end), end, zone);
            var rangeStart = WallClock.ToUtc(from, TimeOnly.MinValue, zone);
            var rangeEnd = to < DateOnly.MaxValue ? WallClock.ToUtc(to.AddDays(1), TimeOnly.MinValue, zone) : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
            if (endUtc > startUtc && startUtc < rangeEnd && endUtc > rangeStart)
            {
                found.Add(new OccurrenceInstant(times.PeriodStart, start, end, startUtc, endUtc));
            }

            return found;
        }

        var first = times.PeriodStart > from ? times.PeriodStart : from;
        var last = times.PeriodEnd < to ? times.PeriodEnd : to;
        if (times.Schedule != JobSchedule.Recurring || last < first)
        {
            return found;
        }

        foreach (var occurrence in Occurrences(times.Weekly, first, last))
        {
            var startUtc = WallClock.ToUtc(occurrence.Date, occurrence.Start, zone);
            var endUtc = WallClock.ToUtc(occurrence.Date, occurrence.End, zone);
            if (endUtc > startUtc)
            {
                found.Add(new OccurrenceInstant(occurrence.Date, occurrence.Start, occurrence.End, startUtc, endUtc));
            }
        }

        return found;
    }

    public static IEnumerable<JobOccurrence> DaySegments(OccurrenceInstant occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!occurrence.CrossesMidnight)
        {
            return [new JobOccurrence(occurrence.Date, occurrence.Start, occurrence.End)];
        }

        return occurrence.End == TimeOnly.MinValue
            ? [new JobOccurrence(occurrence.Date, occurrence.Start, TimeOnly.MinValue)]
            : [new JobOccurrence(occurrence.Date, occurrence.Start, TimeOnly.MinValue), new JobOccurrence(occurrence.EndDate, TimeOnly.MinValue, occurrence.End)];
    }

    public static (DateTime StartUtc, DateTime EndUtc) Window(JobTimes times, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(zone);
        if (times is { IsSingle: true, StartTime: { } start, EndTime: { } end })
        {
            return (WallClock.ToUtc(times.PeriodStart, start, zone), WallClock.ToUtc(SingleLastDay(times.PeriodStart, start, end), end, zone));
        }

        var endUtc = times.PeriodEnd < DateOnly.MaxValue ? WallClock.ToUtc(times.PeriodEnd.AddDays(1), TimeOnly.MinValue, zone) : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        return (WallClock.ToUtc(times.PeriodStart, TimeOnly.MinValue, zone), endUtc);
    }

    public static bool HasStarted(JobTimes times, DateTime nowUtc, TimeZoneInfo zone) => nowUtc >= Window(times, zone).StartUtc;

    public static bool HasEnded(JobTimes times, DateTime nowUtc, TimeZoneInfo zone) => nowUtc >= Window(times, zone).EndUtc;

    public static JobState? ClockTarget(JobState state, JobTimes times, DateTime nowUtc, TimeZoneInfo zone) => state switch
    {
        JobState.Confirmed when HasStarted(times, nowUtc, zone) => JobState.InProgress,
        JobState.InProgress when HasEnded(times, nowUtc, zone) => JobState.Completed,
        _ => null,
    };

    public static string WhenText(JobTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times is { IsSingle: true, StartTime: { } start, EndTime: { } end })
        {
            var text = $"{ScheduleRules.DateText(times.PeriodStart)} {WorkPatternRules.TimeText(start)}-{WorkPatternRules.TimeText(end)}";
            return end <= start ? $"{text} (+1 day)" : text;
        }

        return times.IsSingle ? ScheduleRules.DateText(times.PeriodStart) : PeriodText(times.PeriodStart, times.PeriodEnd);
    }
}
