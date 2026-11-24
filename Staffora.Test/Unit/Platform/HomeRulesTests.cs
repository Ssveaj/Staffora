using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;

namespace Staffora.Test.Unit.Platform;

public class HomeRulesTests
{
    private static readonly DateOnly Monday = new(2026, 11, 16);

    [Fact]
    public void A_calm_day_reads_calm_and_a_busy_day_reads_amber()
    {
        var calm = new HomeStaffing(JobsThisWeek: 4, WeekSlotsHeld: 9, WeekSlotsOpen: 0, OpenSlots: 3, OpenSlotsSoon: 0);
        Assert.Equal(HomeTone.Calm, HomeRules.OpenSlotsTone(calm));
        Assert.Equal(HomeTone.Calm, HomeRules.SlotRingTone(calm));
        Assert.Equal(HomeTone.Calm, HomeRules.WaitingTone(0));
        Assert.Equal(("All held", "9 slots this week"), (HomeRules.SlotRingTitle(calm), HomeRules.SlotRingCaption(calm)));

        var busy = new HomeStaffing(JobsThisWeek: 6, WeekSlotsHeld: 12, WeekSlotsOpen: 3, OpenSlots: 5, OpenSlotsSoon: 2);
        Assert.Equal(HomeTone.Attention, HomeRules.OpenSlotsTone(busy));
        Assert.Equal(HomeTone.Attention, HomeRules.SlotRingTone(busy));
        Assert.Equal(HomeTone.Attention, HomeRules.WaitingTone(2));
        Assert.Equal(("12 of 15", "slots held this week"), (HomeRules.SlotRingTitle(busy), HomeRules.SlotRingCaption(busy)));

        var empty = new HomeStaffing(0, 0, 0, 0, 0);
        Assert.Equal(("No slots", "on this week's jobs"), (HomeRules.SlotRingTitle(empty), HomeRules.SlotRingCaption(empty)));
    }

    [Theory]
    [InlineData(0, 0, "Free week", "nothing assigned")]
    [InlineData(5, 3, "3 to go", "of 5 this week")]
    [InlineData(5, 0, "All done", "5 times this week")]
    [InlineData(1, 0, "All done", "1 time this week")]
    public void The_week_ring_says_what_is_left_of_the_week(int assigned, int toCome, string title, string caption)
    {
        Assert.Equal((title, caption), (HomeRules.WeekRingTitle(assigned, toCome), HomeRules.WeekRingCaption(assigned, toCome)));
    }

    [Fact]
    public void The_next_times_are_the_assigned_ones_not_over_yet_the_earliest_first_and_a_time_past_midnight_once()
    {
        var assignment = Guid.NewGuid();
        var night = Guid.NewGuid();
        var times = new[]
        {
            Time(Monday, assignment, 6, 0, 8, 30),
            Time(Monday, assignment, 8, 30, 10, 0),
            Time(Monday.AddDays(1), Guid.NewGuid(), 6, 0, 8, 0, SlotAssignmentState.Requested),
            Time(Monday.AddDays(2), night, 22, 0, 0, 0),
            Time(Monday.AddDays(3), night, 0, 0, 2, 0),
            Time(Monday.AddDays(3), assignment, 7, 0, 9, 0),
        };

        var next = HomeRules.NextUp(times, Monday, new TimeOnly(9, 0), 5);

        Assert.Equal(
            [(Monday, new TimeOnly(8, 30)), (Monday.AddDays(2), new TimeOnly(22, 0)), (Monday.AddDays(3), new TimeOnly(7, 0))],
            next.Select(time => (time.Date, time.Entry.Start)));
        Assert.Single(HomeRules.NextUp(times, Monday, new TimeOnly(9, 0), 1));
    }

    [Fact]
    public void A_day_and_a_time_read_from_today()
    {
        Assert.Equal("Today 15:00", HomeRules.WhenText(Monday, new TimeOnly(15, 0), Monday));
        Assert.Equal("Tomorrow 06:00", HomeRules.WhenText(Monday.AddDays(1), new TimeOnly(6, 0), Monday));
        Assert.Equal("Thu 19 Nov · 06:30", HomeRules.WhenText(Monday.AddDays(3), new TimeOnly(6, 30), Monday));
        Assert.Equal("Mon", HomeRules.WeekdayText(Monday));
    }

    [Fact]
    public void A_job_state_is_named_as_its_dialog_names_it()
    {
        Assert.Equal("Awaiting coordination", HomeRules.StateName(JobState.AwaitingCoordination));
        Assert.Equal("Confirmed", HomeRules.StateName(JobState.Confirmed));
    }

    private static OwnTime Time(DateOnly date, Guid assignment, int fromHour, int fromMinute, int toHour, int toMinute, SlotAssignmentState state = SlotAssignmentState.Assigned) =>
        new(date, new OwnOccurrenceEntry(assignment, "Office cleaning", "Cleaner", state, new TimeOnly(fromHour, fromMinute), new TimeOnly(toHour, toMinute)));
}
