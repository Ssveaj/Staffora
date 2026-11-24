using Staffora.Core.Staffing;

namespace Staffora.Test.Unit.Staffing;

public class TimelineRulesTests
{
    [Fact]
    public void The_axis_spans_the_whole_day_from_00_to_24()
    {
        Assert.Equal(24, TimelineRules.HoursInDay);
        Assert.Equal(25, TimelineRules.HourMarks.Count);
        Assert.Equal((0, 24), (TimelineRules.HourMarks[0], TimelineRules.HourMarks[^1]));
        Assert.Equal(Enumerable.Range(0, 25), TimelineRules.HourMarks);
    }

    [Fact]
    public void A_time_from_ten_to_half_past_eleven_starts_ten_hours_in_and_is_an_hour_and_a_half_long()
    {
        var place = TimelineRules.Place(new TimeOnly(10, 0), new TimeOnly(11, 30));

        Assert.Equal(new TimelineSpan(10, 1.5), place);

        Assert.Equal(640, TimelineRules.Pixels(place.From, 64));
        Assert.Equal(96, TimelineRules.Pixels(place.Length, 64));
    }

    [Fact]
    public void A_whole_day_fills_the_axis_and_a_stretch_early_or_late_keeps_its_place()
    {
        Assert.Equal(new TimelineSpan(0, 24), TimelineRules.Place(TimeOnly.MinValue, TimeOnly.MinValue));
        Assert.Equal(new TimelineSpan(22, 2), TimelineRules.Place(new TimeOnly(22, 0), TimeOnly.MinValue));

        Assert.Equal(new TimelineSpan(5, 1), TimelineRules.Place(new TimeOnly(5, 0), new TimeOnly(6, 0)));
        Assert.Equal(new TimelineSpan(20.25, 2.5), TimelineRules.Place(new TimeOnly(20, 15), new TimeOnly(22, 45)));
    }

    [Fact]
    public void The_now_line_stands_at_its_minute_on_today_and_on_no_other_day()
    {
        var today = new DateOnly(2026, 11, 9);
        var at = new DateTimeOffset(2026, 11, 9, 9, 40, 30, TimeSpan.FromHours(1));

        var now = TimelineRules.NowOn(at, today);

        Assert.NotNull(now);
        Assert.Equal("09:40", now.Text);
        Assert.Equal(Math.Round(9 + (40 / 60d), 4), now.At);
        Assert.Null(TimelineRules.NowOn(at, today.AddDays(1)));
        Assert.Null(TimelineRules.NowOn(at, today.AddDays(-1)));

        Assert.Equal("23:59", TimelineRules.NowOn(new DateTimeOffset(2026, 11, 9, 23, 59, 0, TimeSpan.Zero), today)!.Text);
        Assert.Equal((0d, "00:00"), TimelineRules.NowOn(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), today.AddDays(1)) is { } midnight ? (midnight.At, midnight.Text) : default);
    }

    [Fact]
    public void A_day_that_is_not_today_opens_at_seven_or_at_its_first_booking_when_that_is_earlier()
    {
        Assert.Equal(7, TimelineRules.OpeningHour([]));
        Assert.Equal(7, TimelineRules.OpeningHour([new TimeOnly(10, 0), new TimeOnly(13, 30)]));
        Assert.Equal(5, TimelineRules.OpeningHour([new TimeOnly(10, 0), new TimeOnly(5, 45)]));
        Assert.Equal(0, TimelineRules.OpeningHour([TimeOnly.MinValue]));
    }

    [Fact]
    public void Overlapping_stretches_take_lanes_one_under_the_other_and_a_lane_is_free_again_once_its_stretch_has_ended()
    {
        TimelineSpan[] spans = [new(1, 2), new(2, 2), new(3, 1), new(5, 1), new(2.5, 0.25)];

        Assert.Equal([0, 1, 0, 0, 2], TimelineRules.Lanes(spans));
        Assert.Empty(TimelineRules.Lanes([]));
    }

    [Fact]
    public void A_days_marks_count_its_slots_by_where_they_stand_on_that_day()
    {
        var monday = new DateOnly(2027, 3, 8);
        var tuesday = monday.AddDays(1);
        WeekFacts[] slots =
        [
            new(PriorityKind.Uncovered, [monday, tuesday], []),
            new(PriorityKind.Unanswered, [monday], []),
            new(PriorityKind.WaitingForPick, [tuesday], []),
            new(PriorityKind.AtRisk, [monday, tuesday], [tuesday]),
            new(PriorityKind.Covered, [monday], []),
            new(PriorityKind.AwaitsReply, [monday.AddDays(30)], []),
        ];

        var marks = PriorityRules.MarkDays(slots, monday, monday.AddDays(6));

        Assert.Equal([monday, tuesday], marks.Keys.Order());
        Assert.Equal(new DayMark(monday, 2, 0, 0, 2), marks[monday]);
        Assert.Equal(new DayMark(tuesday, 1, 1, 1, 0), marks[tuesday]);
        Assert.Equal(4, marks[monday].Total);
    }
}
