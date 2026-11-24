using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class StaffingRulesTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static readonly OccurrenceTimes[] Timetable =
    [
        new(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)),
        new(Weekday.Wednesday, new TimeOnly(9, 0), new TimeOnly(10, 0)),
    ];

    [Fact]
    public void The_occurrences_judged_are_the_periods_not_yet_over_on_the_tenants_calendar_and_clock()
    {
        var (start, end) = (new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 14));

        var all = StaffingRules.RemainingOccurrences(Timetable, start, end, Utc(2027, 1, 20, 12, 0), Stockholm);
        Assert.Equal(["2027-02-01 08:00-10:00", "2027-02-03 09:00-10:00", "2027-02-08 08:00-10:00", "2027-02-10 09:00-10:00"], all.Select(occurrence => occurrence.Text));
        Assert.Equal((Utc(2027, 2, 1, 7, 0), Utc(2027, 2, 1, 9, 0)), (all[0].StartUtc, all[0].EndUtc));

        var running = StaffingRules.RemainingOccurrences(Timetable, start, end, Utc(2027, 2, 3, 8, 30), Stockholm);
        Assert.Equal(["2027-02-03 09:00-10:00", "2027-02-08 08:00-10:00", "2027-02-10 09:00-10:00"], running.Select(occurrence => occurrence.Text));
        Assert.Equal((new DateOnly(2027, 2, 3), new DateOnly(2027, 2, 14)), StaffingRules.RemainingRange(start, end, Utc(2027, 2, 3, 8, 30), Stockholm));

        Assert.Empty(StaffingRules.RemainingOccurrences(Timetable, start, end, Utc(2027, 2, 10, 9, 0), Stockholm));
        Assert.Null(StaffingRules.RemainingRange(start, end, Utc(2027, 2, 15, 0, 0), Stockholm));

        Assert.Empty(StaffingRules.RemainingOccurrences([new OccurrenceTimes(Weekday.Sunday, new TimeOnly(2, 0), new TimeOnly(2, 30))], new DateOnly(2027, 3, 28), new DateOnly(2027, 3, 28), Utc(2027, 3, 1, 0, 0), Stockholm));
    }

    [Fact]
    public void A_verdict_reads_the_blocks_and_warnings_that_meet_an_occurrence_and_leaves_the_own_assignment_out()
    {
        var occurrences = StaffingRules.RemainingOccurrences(Timetable, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 14), Utc(2027, 1, 20, 12, 0), Stockholm);
        var (absence, own, other, request) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var staff = new StaffAvailability(
            Guid.NewGuid(),
            WorkingOn(occurrences),
            [
                new AvailabilityBlock(Utc(2027, 2, 3, 0, 0), Utc(2027, 2, 4, 0, 0), "On sick leave", absence),
                new AvailabilityBlock(Utc(2027, 2, 1, 5, 0), Utc(2027, 2, 1, 7, 0), "On planned leave", Guid.NewGuid()),

                new AvailabilityBlock(Utc(2027, 2, 8, 7, 0), Utc(2027, 2, 8, 9, 0), AvailabilityRules.AssignedReason, own),
                new AvailabilityBlock(Utc(2027, 2, 8, 8, 30), Utc(2027, 2, 8, 9, 30), AvailabilityRules.AssignedReason, other),
            ],
            [new AvailabilityWarning(Utc(2027, 2, 10, 8, 0), Utc(2027, 2, 10, 9, 0), AvailabilityRules.RequestedReason, request)]);

        var verdict = StaffingRules.Judge(staff, occurrences, own);

        Assert.True(verdict.IsBlocked);
        Assert.Equal((4, 2), (verdict.Occurrences, verdict.BlockedOccurrences));
        Assert.Equal([("On sick leave", absence, "2027-02-03 09:00-10:00"), (AvailabilityRules.AssignedReason, other, "2027-02-08 08:00-10:00")], verdict.Blocks.Select(finding => (finding.Reason, finding.SourceId!.Value, finding.Occurrence.Text)));
        Assert.Equal([(AvailabilityRules.RequestedReason, (Guid?)request, "2027-02-10 09:00-10:00")], verdict.Warnings.Select(finding => (finding.Reason, finding.SourceId, finding.Occurrence.Text)));
        Assert.Equal("Blocked on 2 of 4 occurrences", verdict.Summary);
        Assert.Equal("The staff member is not free for 2 of the job's 4 remaining occurrences. On sick leave: 1 occurrence, 2027-02-03. Assigned to work a job: 1 occurrence, 2027-02-08.", verdict.BlockedText);

        Assert.Contains(StaffingRules.Judge(staff, occurrences).Blocks, finding => finding.SourceId == own);
    }

    [Fact]
    public void An_occurrence_outside_the_work_pattern_warns_only_where_nothing_blocks_and_a_staff_member_out_of_service_is_blocked_on_every_occurrence()
    {
        var occurrences = StaffingRules.RemainingOccurrences(Timetable, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 7), Utc(2027, 1, 20, 12, 0), Stockholm);

        var mondays = new StaffAvailability(Guid.NewGuid(), WorkingOn([occurrences[0]]), [], []);
        var verdict = StaffingRules.Judge(mondays, occurrences);
        Assert.False(verdict.IsBlocked);
        Assert.Equal([(StaffingRules.OutsidePatternReason, "2027-02-03 09:00-10:00")], verdict.Warnings.Select(finding => (finding.Reason, finding.Occurrence.Text)));
        Assert.Equal("Free, a warning on 1 of 2 occurrences", verdict.Summary);
        Assert.Null(verdict.BlockedText);

        var inactive = new StaffAvailability(Guid.NewGuid(), [], [new AvailabilityBlock(Utc(2027, 1, 31, 23, 0), Utc(2027, 2, 7, 23, 0), AbsenceRules.NotInServiceReason)], []);
        var outOfService = StaffingRules.Judge(inactive, occurrences, Guid.NewGuid());
        Assert.Equal((2, 0), (outOfService.BlockedOccurrences, outOfService.Warnings.Count));
        Assert.Equal(["The staff member is inactive: 2 occurrences, 2027-02-01 to 2027-02-03"], outOfService.BlockGroups.Select(group => group.Text));

        Assert.Equal("Free for all 2 occurrences", StaffingRules.Judge(new StaffAvailability(Guid.NewGuid(), WorkingOn(occurrences), [], []), occurrences).Summary);
        Assert.Equal("No occurrences left in the period", StaffingVerdict.None.Summary);
    }

    [Fact]
    public void One_blocked_occurrence_of_many_blocks_the_staff_member_for_the_slot()
    {
        var occurrences = StaffingRules.RemainingOccurrences(Timetable, new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Utc(2027, 1, 20, 12, 0), Stockholm);
        var absent = new StaffAvailability(Guid.NewGuid(), WorkingOn(occurrences), [new AvailabilityBlock(Utc(2027, 3, 8, 0, 0), Utc(2027, 3, 9, 0, 0), "On planned leave", Guid.NewGuid())], []);

        var verdict = StaffingRules.Judge(absent, occurrences);

        Assert.True(occurrences.Count > 30);
        Assert.True(verdict.IsBlocked);
        Assert.Equal($"Blocked on 1 of {occurrences.Count} occurrences", verdict.Summary);
    }

    [Fact]
    public void Qualifications_meet_a_slot_as_stored_sets_an_archived_one_counted_on_either_side()
    {
        var (mathematics, physics, archived) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal((2, 2, true), Shape(StaffingRules.Match([mathematics, archived], new HashSet<Guid> { mathematics, archived, physics })));
        Assert.Equal((2, 1, false), Shape(StaffingRules.Match([mathematics, archived], new HashSet<Guid> { mathematics })));
        Assert.Equal([archived], StaffingRules.Match([mathematics, archived], new HashSet<Guid> { mathematics }).Missing);
        Assert.Equal((0, 0, true), Shape(StaffingRules.Match([], new HashSet<Guid>())));

        static (int, int, bool) Shape(QualificationMatch match) => (match.Required, match.Matching, match.MeetsAll);
    }

    [Fact]
    public void The_ranking_puts_every_required_qualification_first_then_more_matches_then_fewer_held_jobs_then_the_name()
    {
        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToList();
        RankFacts[] candidates =
        [
            new(ids[0], "Zed", MeetsAllRequired: false, MatchingQualifications: 1, HeldJobs: 0),
            new(ids[1], "Ann", MeetsAllRequired: false, MatchingQualifications: 0, HeldJobs: 0),
            new(ids[2], "Yan", MeetsAllRequired: true, MatchingQualifications: 2, HeldJobs: 3),
            new(ids[3], "Bea", MeetsAllRequired: true, MatchingQualifications: 2, HeldJobs: 1),
            new(ids[4], "Cal", MeetsAllRequired: true, MatchingQualifications: 2, HeldJobs: 1),
            new(ids[5], "cal", MeetsAllRequired: true, MatchingQualifications: 2, HeldJobs: 1),
            new(ids[6], "Dee", MeetsAllRequired: false, MatchingQualifications: 1, HeldJobs: 2),
        ];

        Assert.Equal(
            [("Bea", ids[3]), ("Cal", ids[4]), ("cal", ids[5]), ("Yan", ids[2]), ("Zed", ids[0]), ("Dee", ids[6]), ("Ann", ids[1])],
            StaffingRules.Rank(candidates).Select(fact => (fact.Name, fact.ResourceId)));

        Assert.Equal("Yan", StaffingRules.Rank([candidates[0], candidates[2]])[0].Name);
    }

    [Fact]
    public void Findings_are_grouped_by_reason_and_source_with_their_occurrences_and_dates()
    {
        var occurrences = StaffingRules.RemainingOccurrences(Timetable, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 14), Utc(2027, 1, 20, 12, 0), Stockholm);
        var source = Guid.NewGuid();
        var groups = StaffingRules.Group([.. occurrences.Take(3).Select(occurrence => new OccurrenceFinding(occurrence, "On sick leave", source)), new OccurrenceFinding(occurrences[3], "On sick leave", Guid.NewGuid())]);

        Assert.Equal(["On sick leave: 3 occurrences, 2027-02-01 to 2027-02-08", "On sick leave: 1 occurrence, 2027-02-10"], groups.Select(group => group.Text));
        Assert.Equal("On sick leave (Physics, Lead): 3 occurrences, 2027-02-01 to 2027-02-08", groups[0].TextAbout("Physics, Lead"));
        Assert.Equal("On sick leave: 2 occurrences on 2027-02-01", new FindingGroup("On sick leave", null, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 1), 2).Text);
    }

    private static List<WorkingTime> WorkingOn(IEnumerable<OccurrenceInstant> occurrences) =>
        [.. occurrences.Select(occurrence => new WorkingTime(occurrence.Date, occurrence.Start, occurrence.End, occurrence.StartUtc, occurrence.EndUtc, Guid.Empty, Guid.Empty))];

    private static DateTime Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
