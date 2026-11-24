using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class BoardRulesTests
{
    private static readonly Guid Staff = Guid.NewGuid();
    private static readonly Guid Physics = Guid.NewGuid();
    private static readonly Guid Maths = Guid.NewGuid();

    [Fact]
    public void The_days_are_Monday_to_Sunday_of_the_week_of_any_day()
    {
        var expected = Enumerable.Range(0, 7).Select(offset => new DateOnly(2027, 3, 8).AddDays(offset)).ToArray();
        Assert.Equal(expected, BoardRules.Days(new DateOnly(2027, 3, 8)));
        Assert.Equal(expected, BoardRules.Days(new DateOnly(2027, 3, 14)));
        Assert.Equal(BoardRules.DaysInWeek, BoardRules.Days(new DateOnly(2027, 3, 10)).Count);
    }

    [Theory]
    [InlineData(PriorityKind.Uncovered, true)]
    [InlineData(PriorityKind.Unanswered, true)]
    [InlineData(PriorityKind.WaitingForPick, false)]
    [InlineData(PriorityKind.AtRisk, false)]
    [InlineData(PriorityKind.Covered, false)]
    public void A_slot_nobody_holds_is_to_staff_and_a_yes_or_a_holder_holds_it(PriorityKind kind, bool toStaff) =>
        Assert.Equal(toStaff, BoardRules.IsToStaff(kind));

    [Fact]
    public void A_staff_member_not_asked_is_judged_exactly_as_the_staffing_dialog_judges_them()
    {
        var occurrences = Occurrences(new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 15), new DateOnly(2027, 3, 22));
        var sick = new AvailabilityBlock(Utc(2027, 3, 15, 0), Utc(2027, 3, 16, 0), "On sick leave (reported)", Guid.NewGuid());
        var request = new AvailabilityWarning(Utc(2027, 3, 22, 8), Utc(2027, 3, 22, 9), "Asked to work a job, not answered yet", Guid.NewGuid());
        var working = occurrences.Where(occurrence => occurrence.Date != new DateOnly(2027, 3, 22)).Select(occurrence => new WorkingTime(occurrence.Date, new TimeOnly(7, 0), new TimeOnly(16, 0), occurrence.StartUtc.AddHours(-1), occurrence.StartUtc.AddHours(8), Guid.NewGuid(), Guid.NewGuid())).ToList();
        var staff = new StaffAvailability(Staff, working, [sick], [request]);

        var pair = BoardRules.Pair(staff, occurrences, [Physics, Maths], new HashSet<Guid> { Physics }, asked: false);
        var dialog = StaffingRules.Judge(staff, occurrences);

        Assert.False(pair.Asked);
        Assert.False(pair.IsFree);
        Assert.Equal(dialog.Summary, pair.Verdict.Summary);
        Assert.Equal(dialog.BlockedText, pair.Verdict.BlockedText);
        Assert.Equal(dialog.BlockGroups, pair.Verdict.BlockGroups);
        Assert.Equal(dialog.WarningGroups, pair.Verdict.WarningGroups);
        Assert.Equal((2, 1), (pair.Match.Required, pair.Match.Matching));
        Assert.Equal([Maths], pair.Match.Missing);
    }

    [Fact]
    public void A_staff_member_asked_already_is_the_slots_people_and_no_candidate_and_no_occurrences_judge_nobody()
    {
        var occurrences = Occurrences(new DateOnly(2027, 3, 8));
        var blocked = new StaffAvailability(Staff, [], [new AvailabilityBlock(Utc(2027, 3, 8, 0), Utc(2027, 3, 9, 0), "The staff member is inactive")], []);

        var asked = BoardRules.Pair(blocked, occurrences, [], new HashSet<Guid>(), asked: true);
        Assert.True(asked.Asked);
        Assert.False(asked.IsFree);
        Assert.Same(StaffingVerdict.None, asked.Verdict);

        var none = BoardRules.Pair(blocked, [], [], new HashSet<Guid>(), asked: false);
        Assert.True(none.IsFree);
        Assert.Same(StaffingVerdict.None, none.Verdict);

        var free = BoardRules.Pair(new StaffAvailability(Staff, [], [], []), occurrences, [], new HashSet<Guid>(), asked: false);
        Assert.True(free.IsFree);
        Assert.Equal(StaffingRules.Judge(new StaffAvailability(Staff, [], [], []), occurrences).Summary, free.Verdict.Summary);
    }

    [Fact]
    public void A_long_range_is_cut_into_reads_of_at_most_the_longest_and_a_reversed_one_into_none()
    {
        var from = new DateOnly(2027, 1, 1);
        Assert.Equal([(from, from.AddDays(9))], BoardRules.Windows(from, from.AddDays(9), 731));
        Assert.Equal([(from, from.AddDays(730)), (from.AddDays(731), from.AddDays(799))], BoardRules.Windows(from, from.AddDays(799), 731));
        Assert.Empty(BoardRules.Windows(from, from.AddDays(-1), 731));
        Assert.Equal([(DateOnly.MaxValue.AddDays(-1), DateOnly.MaxValue)], BoardRules.Windows(DateOnly.MaxValue.AddDays(-1), DateOnly.MaxValue, 731));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoardRules.Windows(from, from, 0));
    }

    private static List<OccurrenceInstant> Occurrences(params DateOnly[] days) =>
        [.. days.Select(day => new OccurrenceInstant(day, new TimeOnly(8, 0), new TimeOnly(10, 0), Utc(day.Year, day.Month, day.Day, 8), Utc(day.Year, day.Month, day.Day, 10)))];

    private static DateTime Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);
}
