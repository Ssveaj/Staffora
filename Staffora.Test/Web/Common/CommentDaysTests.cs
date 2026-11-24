using Staffora.Blazor.Components.Common.Comments;

namespace Staffora.Test.Web.Common;

public class CommentDaysTests
{
    private static readonly DateOnly today = new(2027, 3, 10);

    [Fact]
    public void The_day_line_says_today_yesterday_a_weekday_and_the_year_only_when_it_is_another()
    {
        Assert.Equal("Today", CommentDays.DayLabel(today, today));
        Assert.Equal("Yesterday", CommentDays.DayLabel(today.AddDays(-1), today));
        Assert.Equal("Monday 8 March", CommentDays.DayLabel(new DateOnly(2027, 3, 8), today));
        Assert.Equal("10 November 2026", CommentDays.DayLabel(new DateOnly(2026, 11, 10), today));
    }

    [Fact]
    public void A_notes_time_says_its_day_and_time()
    {
        var offset = TimeSpan.FromHours(1);
        Assert.Equal("today 08:12", CommentDays.WhenLabel(new DateTimeOffset(2027, 3, 10, 8, 12, 0, offset), today));
        Assert.Equal("yesterday 16:40", CommentDays.WhenLabel(new DateTimeOffset(2027, 3, 9, 16, 40, 0, offset), today));
        Assert.Equal("8 Mar 09:30", CommentDays.WhenLabel(new DateTimeOffset(2027, 3, 8, 9, 30, 0, offset), today));
        Assert.Equal("10 Nov 2026 09:30", CommentDays.WhenLabel(new DateTimeOffset(2026, 11, 10, 9, 30, 0, offset), today));
    }

    [Fact]
    public void Today_is_the_organizations_day_at_the_instant()
    {
        var late = new DateTimeOffset(2027, 3, 9, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2027, 3, 10), CommentDays.Today(late, "Europe/Stockholm"));
        Assert.Equal(new DateOnly(2027, 3, 9), CommentDays.Today(late, "America/New_York"));
    }
}
