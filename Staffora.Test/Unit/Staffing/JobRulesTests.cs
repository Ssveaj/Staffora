using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;

namespace Staffora.Test.Unit.Staffing;

public class JobRulesTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    [Fact]
    public void A_period_is_in_order_given_and_at_most_a_year()
    {
        Assert.Empty(JobRules.PeriodProblems(new DateOnly(2027, 1, 11), new DateOnly(2027, 1, 11)));
        Assert.Empty(JobRules.PeriodProblems(new DateOnly(2028, 1, 1), new DateOnly(2028, 12, 31)));
        Assert.Equal([JobRules.PeriodEndsBeforeStart], JobRules.PeriodProblems(new DateOnly(2027, 1, 11), new DateOnly(2027, 1, 10)));
        Assert.Equal([JobRules.PeriodTooLong], JobRules.PeriodProblems(new DateOnly(2027, 1, 1), new DateOnly(2028, 1, 2)));
        Assert.Equal([JobRules.PeriodStartMissing, JobRules.PeriodEndMissing], JobRules.PeriodProblems(default, default));
    }

    [Fact]
    public void The_tenants_day_starts_at_its_own_local_midnight_across_a_daylight_saving_change()
    {
        Assert.Equal(new DateOnly(2027, 3, 27), JobRules.LocalDay(At("2027-03-27T22:59:59Z"), Stockholm));
        Assert.Equal(new DateOnly(2027, 3, 28), JobRules.LocalDay(At("2027-03-27T23:00:00Z"), Stockholm));
        Assert.Equal(new DateOnly(2027, 3, 28), JobRules.LocalDay(At("2027-03-28T21:59:59Z"), Stockholm));
        Assert.Equal(new DateOnly(2027, 3, 29), JobRules.LocalDay(At("2027-03-28T22:00:00Z"), Stockholm));
        Assert.Equal(new DateOnly(2027, 10, 31), JobRules.LocalDay(At("2027-10-30T22:00:00Z"), Stockholm));
        Assert.Equal(new DateOnly(2027, 10, 31), JobRules.LocalDay(At("2027-10-31T22:59:59Z"), Stockholm));
        Assert.Equal(new DateOnly(2027, 11, 1), JobRules.LocalDay(At("2027-10-31T23:00:00Z"), Stockholm));

        var santiago = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        Assert.Equal(new DateOnly(2027, 9, 4), JobRules.LocalDay(At("2027-09-05T03:59:59Z"), santiago));
        Assert.Equal(new DateOnly(2027, 9, 5), JobRules.LocalDay(At("2027-09-05T04:00:00Z"), santiago));

        Assert.Equal((new DateOnly(2027, 3, 28), new DateOnly(2027, 3, 27)), (JobRules.LocalDay(At("2027-03-27T23:30:00Z"), Stockholm), JobRules.LocalDay(At("2027-03-27T23:30:00Z"), TimeZoneInfo.Utc)));
    }

    [Fact]
    public void The_clock_puts_a_confirmed_job_in_progress_from_its_first_day_and_completes_it_after_its_last()
    {
        var (start, end) = (new DateOnly(2027, 3, 28), new DateOnly(2027, 5, 28));
        var times = JobTimes.Recurring(start, end, []);
        DateTime Midnight(DateOnly day) => WallClock.ToUtc(day, TimeOnly.MinValue, Stockholm);

        Assert.Null(JobRules.ClockTarget(JobState.Confirmed, times, Midnight(start).AddTicks(-1), Stockholm));
        Assert.Equal(JobState.InProgress, JobRules.ClockTarget(JobState.Confirmed, times, Midnight(start), Stockholm));
        Assert.Equal(JobState.InProgress, JobRules.ClockTarget(JobState.Confirmed, times, Midnight(end.AddDays(3)), Stockholm));

        Assert.Null(JobRules.ClockTarget(JobState.InProgress, times, Midnight(end.AddDays(1)).AddTicks(-1), Stockholm));
        Assert.Equal(JobState.Completed, JobRules.ClockTarget(JobState.InProgress, times, Midnight(end.AddDays(1)), Stockholm));

        var oneDay = JobTimes.Recurring(start, start, []);
        Assert.Equal(JobState.InProgress, JobRules.ClockTarget(JobState.Confirmed, oneDay, Midnight(start).AddHours(12), Stockholm));
        Assert.Null(JobRules.ClockTarget(JobState.InProgress, oneDay, Midnight(start).AddHours(12), Stockholm));
        Assert.Equal(JobState.Completed, JobRules.ClockTarget(JobState.InProgress, oneDay, Midnight(start.AddDays(1)), Stockholm));

        foreach (var state in Enum.GetValues<JobState>().Except([JobState.Confirmed, JobState.InProgress]))
        {
            Assert.Null(JobRules.ClockTarget(state, times, Midnight(end.AddDays(30)), Stockholm));
        }
    }

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_timetable_has_each_occurrence_on_a_weekday_starting_before_it_ends_and_overlapping_no_other_of_its_day()
    {
        Assert.Empty(JobRules.TimetableProblems([]));
        Assert.Empty(JobRules.TimetableProblems([Occurrence(Weekday.Monday, 8, 9), Occurrence(Weekday.Monday, 9, 10), Occurrence(Weekday.Tuesday, 8, 9)]));
        Assert.Equal(["Monday: 08:00-10:00 and 09:00-11:00 overlap."], JobRules.TimetableProblems([Occurrence(Weekday.Monday, 9, 11), Occurrence(Weekday.Monday, 8, 10)]));
        Assert.Equal(["Friday: a time (12:00-12:00) must start before it ends."], JobRules.TimetableProblems([Occurrence(Weekday.Friday, 12, 12)]));
        Assert.Equal(["A time (08:00-09:00) is on no day of the week."], JobRules.TimetableProblems([Occurrence((Weekday)0, 8, 9)]));
        Assert.Equal(["A time (08:00-09:00) is on no day of the week."], JobRules.TimetableProblems([Occurrence((Weekday)8, 8, 9)]));
    }

    [Fact]
    public void A_timetable_has_at_most_fifty_occurrences_and_more_is_told_alone()
    {
        OccurrenceTimes[] week = [.. Enumerable.Range(0, JobRules.MaxOccurrences).Select(i => Occurrence((Weekday)(i % 7 + 1), 6 + (i / 7), 7 + (i / 7)))];
        Assert.Equal(50, JobRules.MaxOccurrences);
        Assert.Empty(JobRules.TimetableProblems(week));
        Assert.Equal([JobRules.TooManyOccurrences], JobRules.TimetableProblems([.. week, Occurrence(Weekday.Monday, 6, 7)]));
    }

    [Fact]
    public void The_timetable_repeats_over_the_periods_days_and_keeps_its_wall_clock_times_across_a_clock_change()
    {
        var occurrences = new[] { Occurrence(Weekday.Monday, 8, 9), Occurrence(Weekday.Wednesday, 13, 14) };
        var dated = JobRules.Occurrences(occurrences, new DateOnly(2027, 3, 22), new DateOnly(2027, 3, 31)).ToList();
        Assert.Equal(
            [
                new JobOccurrence(new DateOnly(2027, 3, 22), new TimeOnly(8, 0), new TimeOnly(9, 0)),
                new JobOccurrence(new DateOnly(2027, 3, 24), new TimeOnly(13, 0), new TimeOnly(14, 0)),
                new JobOccurrence(new DateOnly(2027, 3, 29), new TimeOnly(8, 0), new TimeOnly(9, 0)),
                new JobOccurrence(new DateOnly(2027, 3, 31), new TimeOnly(13, 0), new TimeOnly(14, 0)),
            ],
            dated);
        Assert.Equal(new DateTime(2027, 3, 22, 7, 0, 0, DateTimeKind.Utc), WallClock.ToUtc(dated[0].Date, dated[0].Start, Stockholm));
        Assert.Equal(new DateTime(2027, 3, 29, 6, 0, 0, DateTimeKind.Utc), WallClock.ToUtc(dated[2].Date, dated[2].Start, Stockholm));
        Assert.Empty(JobRules.Occurrences(occurrences, new DateOnly(2027, 3, 23), new DateOnly(2027, 3, 23)));
        Assert.Empty(JobRules.Occurrences(occurrences, new DateOnly(2027, 3, 29), new DateOnly(2027, 3, 28)));
        Assert.Equal(Weekday.Sunday, WeekdayExtensions.WeekdayOf(new DateOnly(2027, 3, 28)));
        Assert.Equal(DayOfWeek.Sunday, Weekday.Sunday.ToDayOfWeek());
        Assert.Equal(DayOfWeek.Monday, Weekday.Monday.ToDayOfWeek());
    }

    [Fact]
    public void Coverage_is_derived_from_the_active_slots_and_is_uncovered_until_one_is_held()
    {
        Assert.Equal(JobCoverage.Uncovered, JobCoverageRules.Of([]));
        Assert.Equal(JobCoverage.Uncovered, JobCoverageRules.Of([new(JobSlotState.Active, false), new(JobSlotState.Active, false)]));
        Assert.Equal(JobCoverage.PartlyCovered, JobCoverageRules.Of([new(JobSlotState.Active, true), new(JobSlotState.Active, false)]));
        Assert.Equal(JobCoverage.Covered, JobCoverageRules.Of([new(JobSlotState.Active, true), new(JobSlotState.Canceled, false)]));
        Assert.Equal(JobCoverage.Uncovered, JobCoverageRules.Of([new(JobSlotState.Canceled, true)]));
        Assert.Equal(["Uncovered", "Partly covered", "Covered"], Enum.GetValues<JobCoverage>().Select(JobCoverageRules.Label));
    }

    [Fact]
    public void A_canceled_slots_staff_members_read_a_dash_as_its_coverage_does()
    {
        Assert.Equal(("—", "—"), Labels(new JobSlotRow(Guid.NewGuid(), Guid.NewGuid(), "Lead", null, [], JobSlotState.Canceled, JobCoverage.Uncovered)));
        Assert.Equal(("—", "—"), Labels(new JobSlotRow(Guid.NewGuid(), Guid.NewGuid(), "Lead", null, [], JobSlotState.Canceled, JobCoverage.Uncovered, Holder: "Ada Example", Requested: 1)));

        Assert.Equal(("Nobody asked yet", "Uncovered"), Labels(new JobSlotRow(Guid.NewGuid(), Guid.NewGuid(), "Lead", null, [], JobSlotState.Active, JobCoverage.Uncovered)));
        Assert.Equal(("2 asked, 1 said yes", "Covered"), Labels(new JobSlotRow(Guid.NewGuid(), Guid.NewGuid(), "Lead", null, [], JobSlotState.Active, JobCoverage.Covered, Requested: 2, Accepted: 1)));
        Assert.Equal(("Ada Example (assigned)", "Covered"), Labels(new JobSlotRow(Guid.NewGuid(), Guid.NewGuid(), "Lead", null, [], JobSlotState.Active, JobCoverage.Covered, Holder: "Ada Example")));

        static (string StaffMembers, string Coverage) Labels(Staffora.Core.Models.JobSlotRow row) => (row.StaffMembersLabel, row.CoverageLabel);
    }

    private static OccurrenceTimes Occurrence(Weekday day, int startHour, int endHour) => new(day, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0));
}
