using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class JobOccurrenceRulesTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    private static readonly DateOnly Day = new(2026, 12, 3);

    [Fact]
    public void A_single_job_across_midnight_is_one_occurrence_ending_the_next_day()
    {
        var times = JobTimes.Single(Day, new TimeOnly(18, 0), new TimeOnly(2, 0));
        Assert.Equal(Day.AddDays(1), times.PeriodEnd);

        var occurrence = Assert.Single(JobRules.Occurrences(times, Day, Day, Stockholm));
        Assert.Equal((Day, Day.AddDays(1), true), (occurrence.Date, occurrence.EndDate, occurrence.CrossesMidnight));
        Assert.Equal((new DateTime(2026, 12, 3, 17, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 4, 1, 0, 0, DateTimeKind.Utc)), (occurrence.StartUtc, occurrence.EndUtc));
        Assert.Equal("2026-12-03 18:00-02:00 (+1 day)", occurrence.Text);

        Assert.Equal(occurrence, Assert.Single(JobRules.Occurrences(times, Day.AddDays(1), Day.AddDays(1), Stockholm)));
        Assert.Empty(JobRules.Occurrences(times, Day.AddDays(2), Day.AddDays(2), Stockholm));
        Assert.Empty(JobRules.Occurrences(times, Day.AddDays(-1), Day.AddDays(-1), Stockholm));

        Assert.Equal(
            [new JobOccurrence(Day, new TimeOnly(18, 0), TimeOnly.MinValue), new JobOccurrence(Day.AddDays(1), TimeOnly.MinValue, new TimeOnly(2, 0))],
            JobRules.DaySegments(occurrence));

        var job = new Job { Schedule = JobSchedule.Single, PeriodStart = Day, PeriodEnd = Day.AddMonths(3), StartTime = new TimeOnly(18, 0), EndTime = new TimeOnly(2, 0) };
        JobRules.Normalize(job);
        Assert.Equal(Day.AddDays(1), job.PeriodEnd);
        Assert.Equal("2026-12-03 18:00-02:00 (+1 day)", JobRules.WhenText(JobTimes.Of(job)));

        Assert.Single(StaffingRules.RemainingOccurrences(times, new DateTime(2026, 12, 3, 22, 0, 0, DateTimeKind.Utc), Stockholm));
        Assert.Single(StaffingRules.RemainingOccurrences(times, new DateTime(2026, 12, 4, 0, 30, 0, DateTimeKind.Utc), Stockholm));
        Assert.Empty(StaffingRules.RemainingOccurrences(times, new DateTime(2026, 12, 4, 1, 0, 0, DateTimeKind.Utc), Stockholm));
    }

    [Fact]
    public void A_single_job_ending_at_midnight_touches_one_day()
    {
        var times = JobTimes.Single(Day, new TimeOnly(18, 0), TimeOnly.MinValue);
        var occurrence = Assert.Single(JobRules.Occurrences(times, Day, Day, Stockholm));
        Assert.Equal(new DateTime(2026, 12, 3, 23, 0, 0, DateTimeKind.Utc), occurrence.EndUtc);
        Assert.Equal([new JobOccurrence(Day, new TimeOnly(18, 0), TimeOnly.MinValue)], JobRules.DaySegments(occurrence));

        Assert.Empty(JobRules.Occurrences(times, Day.AddDays(1), Day.AddDays(1), Stockholm));
        Assert.Equal("2026-12-03 18:00-00:00 (+1 day)", JobRules.WhenText(times));

        var day = Assert.Single(JobRules.Occurrences(JobTimes.Single(Day, new TimeOnly(8, 0), new TimeOnly(12, 0)), Day, Day, Stockholm));
        Assert.Equal([new JobOccurrence(Day, new TimeOnly(8, 0), new TimeOnly(12, 0))], JobRules.DaySegments(day));
        Assert.Equal("2026-12-03 08:00-12:00", JobRules.WhenText(JobTimes.Single(Day, new TimeOnly(8, 0), new TimeOnly(12, 0))));
    }

    [Fact]
    public void A_recurring_period_across_a_daylight_saving_change_keeps_its_wall_clock_times()
    {
        var spring = JobTimes.Recurring(new DateOnly(2027, 3, 14), new DateOnly(2027, 4, 4), [new OccurrenceTimes(Weekday.Sunday, new TimeOnly(8, 0), new TimeOnly(10, 0))]);
        var springStarts = JobRules.Occurrences(spring, new DateOnly(2027, 3, 1), new DateOnly(2027, 4, 30), Stockholm).Select(occurrence => occurrence.StartUtc).ToList();
        Assert.Equal(
            [
                new DateTime(2027, 3, 14, 7, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 3, 21, 7, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 3, 28, 6, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 4, 4, 6, 0, 0, DateTimeKind.Utc),
            ],
            springStarts);

        var autumn = JobTimes.Recurring(new DateOnly(2026, 10, 18), new DateOnly(2026, 11, 1), [new OccurrenceTimes(Weekday.Sunday, new TimeOnly(8, 0), new TimeOnly(10, 0))]);
        var autumnOccurrences = JobRules.Occurrences(autumn, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), Stockholm);
        Assert.Equal(
            [new DateTime(2026, 10, 18, 6, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 7, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 7, 0, 0, DateTimeKind.Utc)],
            autumnOccurrences.Select(occurrence => occurrence.StartUtc));
        Assert.All(autumnOccurrences, occurrence => Assert.Equal(TimeSpan.FromHours(2), occurrence.EndUtc - occurrence.StartUtc));

        Assert.Empty(JobRules.Occurrences(spring, new DateOnly(2027, 4, 5), new DateOnly(2027, 4, 30), Stockholm));
        Assert.All(autumnOccurrences, occurrence => Assert.False(occurrence.CrossesMidnight));
    }

    [Fact]
    public void A_single_job_in_the_spring_gap_is_shortened_or_has_no_occurrence()
    {
        var gap = new DateOnly(2027, 3, 28);
        var shortened = Assert.Single(JobRules.Occurrences(JobTimes.Single(gap, new TimeOnly(2, 30), new TimeOnly(3, 30)), gap, gap, Stockholm));
        Assert.Equal(TimeSpan.FromMinutes(30), shortened.EndUtc - shortened.StartUtc);
        Assert.Equal(new DateTime(2027, 3, 28, 1, 0, 0, DateTimeKind.Utc), shortened.StartUtc);
        Assert.Empty(JobRules.Occurrences(JobTimes.Single(gap, new TimeOnly(2, 10), new TimeOnly(2, 50)), gap, gap, Stockholm));
    }

    [Fact]
    public void Schedule_problems_name_each_missing_part_of_each_kind()
    {
        Assert.Empty(JobRules.ScheduleProblems(JobTimes.Single(Day, new TimeOnly(18, 0), new TimeOnly(2, 0))));
        Assert.Equal(
            [JobRules.SingleDayMissing, JobRules.SingleStartMissing, JobRules.SingleEndMissing],
            JobRules.ScheduleProblems(new JobTimes(JobSchedule.Single, default, default, null, null, [])));
        Assert.Equal([JobRules.SingleTimesEqual], JobRules.ScheduleProblems(new JobTimes(JobSchedule.Single, Day, Day, new TimeOnly(9, 0), new TimeOnly(9, 0), [])));
        Assert.Equal([JobRules.SingleLastDayWrong], JobRules.ScheduleProblems(new JobTimes(JobSchedule.Single, Day, Day, new TimeOnly(18, 0), new TimeOnly(2, 0), [])));
        Assert.Equal(
            [JobRules.SingleHasWeeklyTimes],
            JobRules.ScheduleProblems(new JobTimes(JobSchedule.Single, Day, Day, new TimeOnly(8, 0), new TimeOnly(9, 0), [new OccurrenceTimes(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))])));

        Assert.Empty(JobRules.ScheduleProblems(JobTimes.Recurring(Day, Day.AddMonths(3), [new OccurrenceTimes(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))])));
        Assert.Equal([JobRules.PeriodEndsBeforeStart], JobRules.ScheduleProblems(JobTimes.Recurring(Day, Day.AddDays(-1), [])));
        Assert.Equal([JobRules.RecurringHasTimes], JobRules.ScheduleProblems(new JobTimes(JobSchedule.Recurring, Day, Day.AddMonths(1), new TimeOnly(8, 0), null, [])));
        OccurrenceTimes[] backwards = [new OccurrenceTimes(Weekday.Monday, new TimeOnly(9, 0), new TimeOnly(9, 0))];
        Assert.Equal(JobRules.TimetableProblems(backwards), JobRules.ScheduleProblems(JobTimes.Recurring(Day, Day.AddMonths(1), backwards)));
        Assert.Single(JobRules.ScheduleProblems(JobTimes.Recurring(Day, Day.AddMonths(1), backwards)));

        Assert.Equal([JobRules.ScheduleMissing], JobRules.ScheduleProblems(new JobTimes((JobSchedule)0, Day, Day, null, null, [])));

        Assert.True(JobRules.HasTime(JobTimes.Single(Day, new TimeOnly(8, 0), new TimeOnly(9, 0))));
        Assert.False(JobRules.HasTime(new JobTimes(JobSchedule.Single, Day, Day, new TimeOnly(8, 0), null, [])));
        Assert.False(JobRules.HasTime(JobTimes.Recurring(Day, Day.AddMonths(1), [])));
    }

    [Fact]
    public void The_clock_window_of_a_single_job_is_its_times_and_of_a_recurring_job_its_days()
    {
        var times = JobTimes.Single(Day, new TimeOnly(18, 0), new TimeOnly(2, 0));
        Assert.Equal((new DateTime(2026, 12, 3, 17, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 4, 1, 0, 0, DateTimeKind.Utc)), JobRules.Window(times, Stockholm));
        Assert.Null(JobRules.ClockTarget(JobState.Confirmed, times, new DateTime(2026, 12, 3, 16, 59, 0, DateTimeKind.Utc), Stockholm));
        Assert.Equal(JobState.InProgress, JobRules.ClockTarget(JobState.Confirmed, times, new DateTime(2026, 12, 3, 17, 0, 0, DateTimeKind.Utc), Stockholm));
        Assert.Null(JobRules.ClockTarget(JobState.InProgress, times, new DateTime(2026, 12, 4, 0, 59, 0, DateTimeKind.Utc), Stockholm));
        Assert.Equal(JobState.Completed, JobRules.ClockTarget(JobState.InProgress, times, new DateTime(2026, 12, 4, 1, 0, 0, DateTimeKind.Utc), Stockholm));

        Assert.Equal(JobState.InProgress, JobRules.ClockTarget(JobState.Confirmed, times, new DateTime(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc), Stockholm));

        var recurring = JobTimes.Recurring(Day, Day.AddDays(7), []);
        Assert.Equal((WallClock.ToUtc(Day, TimeOnly.MinValue, Stockholm), WallClock.ToUtc(Day.AddDays(8), TimeOnly.MinValue, Stockholm)), JobRules.Window(recurring, Stockholm));
        Assert.Equal(JobState.InProgress, JobRules.ClockTarget(JobState.Confirmed, recurring, new DateTime(2026, 12, 2, 23, 0, 0, DateTimeKind.Utc), Stockholm));
        Assert.Null(JobRules.ClockTarget(JobState.Confirmed, recurring, new DateTime(2026, 12, 2, 22, 59, 0, DateTimeKind.Utc), Stockholm));
    }

    [Fact]
    public void The_availability_books_a_single_jobs_whole_occurrence_on_both_of_its_days()
    {
        var staff = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        var booking = new ScheduledBooking(assignment, staff, SlotAssignmentState.Assigned, JobTimes.Single(Day, new TimeOnly(18, 0), new TimeOnly(2, 0)));

        var answer = AvailabilityRules.Compute([new ScheduledStaff(staff, InService: true)], [], new Dictionary<Guid, PatternShape>(), [], [booking], Day.AddDays(1), Day.AddDays(1), Stockholm);
        var block = Assert.Single(answer[staff].Blocks);
        Assert.Equal((new DateTime(2026, 12, 3, 17, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 4, 1, 0, 0, DateTimeKind.Utc), (Guid?)assignment), (block.StartUtc, block.EndUtc, block.SourceId));
    }
}
