using Bunit;
using Staffora.Blazor.Components.Coordination;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Test.Web.Coordination;

public sealed class CalendarRailTests : WebTestContext
{
    private static readonly DateOnly Today = new(2026, 11, 18);
    private static readonly DateOnly Open = new(2026, 11, 24);
    private static readonly DateOnly Risk = new(2026, 11, 26);
    private static readonly DateOnly Staffed = new(2026, 11, 27);

    [Fact]
    public void A_days_button_selects_that_day_a_week_number_is_no_button_and_each_day_carries_one_dot_for_where_its_slots_stand()
    {
        DateOnly? day = null;
        var marks = new Dictionary<DateOnly, DayMark>
        {
            [Open] = new(Open, 1, 0, 0, 2),
            [Risk] = new(Risk, 1, 1, 0, 0),
            [Staffed] = new(Staffed, 0, 0, 0, 3),
        };
        var rail = Render<CalendarRail>(parameters => parameters
            .Add(component => component.Today, Today)
            .Add(component => component.Shown, Today)
            .Add(component => component.Marks, (from, to) => marks.Where(mark => mark.Key >= from && mark.Key <= to).ToDictionary())
            .Add(component => component.OnDay, chosen => day = chosen));

        Assert.Equal(["2026-11-01", "2026-12-01", "2027-01-01"], rail.FindAll("table.mini-month").Select(month => month.GetAttribute("data-month")));
        var today = rail.Find("button.mini-month__day[data-day='2026-11-18']");
        Assert.Equal(("date", "true"), (today.GetAttribute("aria-current"), today.GetAttribute("aria-pressed")));
        Assert.Contains("mini-month__week-row--shown", today.Closest("tr")!.ClassName, StringComparison.Ordinal);

        var open = rail.Find("button.mini-month__day[data-day='2026-11-24']");
        Assert.Equal(["open"], open.QuerySelectorAll("[data-mark]").Select(mark => mark.GetAttribute("data-mark")));
        Assert.Equal("Tuesday 24 November 2026, 1 open, 2 staffed", open.GetAttribute("aria-label"));
        Assert.Equal(["risk"], rail.Find("button.mini-month__day[data-day='2026-11-26']").QuerySelectorAll("[data-mark]").Select(mark => mark.GetAttribute("data-mark")));
        Assert.Equal(["staffed"], rail.Find("button.mini-month__day[data-day='2026-11-27']").QuerySelectorAll("[data-mark]").Select(mark => mark.GetAttribute("data-mark")));
        Assert.Empty(rail.Find("button.mini-month__day[data-day='2026-11-25']").QuerySelectorAll("[data-mark]"));

        Assert.Empty(rail.FindAll("button.mini-month__week, .mini-month__week-cell button"));
        var number = rail.Find(".mini-month__week[data-week='2026-11-16']");
        Assert.Equal("Week 47", number.TextContent.Trim());
        Assert.Contains("mini-month__week-row--shown", number.Closest("tr")!.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("mini-month__week-row--shown", rail.Find(".mini-month__week[data-week='2026-11-23']").Closest("tr")!.ClassName, StringComparison.Ordinal);

        rail.Find("button.mini-month__day[data-day='2026-11-24']").Click();
        Assert.Equal(Open, day);
        rail.Render(parameters => parameters.Add(component => component.Shown, Open));
        Assert.Equal("true", rail.Find("button.mini-month__day[data-day='2026-11-24']").GetAttribute("aria-pressed"));
        Assert.Single(rail.FindAll("button.mini-month__day[aria-pressed='true']"));
        Assert.Contains("mini-month__week-row--shown", rail.Find(".mini-month__week[data-week='2026-11-23']").Closest("tr")!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_day_is_striped_and_named_in_its_days_accessible_label_and_the_legend_names_the_mark()
    {
        var holiday = new ClosedDayFact(Guid.NewGuid(), "Founders' Day", Open, Open, null);
        var study = new ClosedDayFact(Guid.NewGuid(), "Study days", Open, Risk, "Region West");
        var rail = Render<CalendarRail>(parameters => parameters
            .Add(component => component.Today, Today)
            .Add(component => component.Shown, Today)
            .Add(component => component.Marks, (from, to) => new Dictionary<DateOnly, DayMark> { [Open] = new(Open, 1, 0, 0, 0) })
            .Add(component => component.Closed, (from, to) => ClosedDayRules.MarkDays([holiday, study], from, to)));

        var open = rail.Find("button.mini-month__day[data-day='2026-11-24']");
        Assert.Contains("mini-month__day--closed", open.ClassName, StringComparison.Ordinal);
        Assert.Equal("Tuesday 24 November 2026, Closed for Founders' Day, Closed for Study days in Region West, 1 open", open.GetAttribute("aria-label"));
        Assert.Equal("Closed for Founders' Day, Closed for Study days in Region West", open.GetAttribute("data-closed-day"));
        Assert.Equal("Wednesday 25 November 2026, Closed for Study days in Region West", rail.Find("button.mini-month__day[data-day='2026-11-25']").GetAttribute("aria-label"));
        Assert.Equal(3, rail.FindAll("button.mini-month__day--closed").Count);
        Assert.DoesNotContain("mini-month__day--closed", rail.Find("button.mini-month__day[data-day='2026-11-27']").ClassName, StringComparison.Ordinal);
        Assert.Contains("Closed day", rail.Find(".calendar-rail__legend").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_arrows_name_the_month_they_show_and_move_the_stack_one_month()
    {
        var rail = Render<CalendarRail>(parameters => parameters
            .Add(component => component.Today, Today)
            .Add(component => component.Shown, Today));
        Assert.Equal("Show one month earlier, October", rail.Find("button[data-calendar-move='earlier']").GetAttribute("aria-label"));
        Assert.Equal("Show one month later, February", rail.Find("button[data-calendar-move='later']").GetAttribute("aria-label"));
        Assert.Equal("October", rail.Find("button[data-calendar-move='earlier'] > span[aria-hidden]:last-child").TextContent.Trim());

        rail.Find("button[data-calendar-move='later']").Click();
        Assert.Equal(["2026-12-01", "2027-01-01", "2027-02-01"], rail.FindAll("table.mini-month").Select(month => month.GetAttribute("data-month")));
        Assert.Equal("Show one month later, March", rail.Find("button[data-calendar-move='later']").GetAttribute("aria-label"));

        rail.Find("button[data-calendar-move='earlier']").Click();
        rail.Find("button[data-calendar-move='earlier']").Click();
        Assert.Equal("2026-10-01", rail.FindAll("table.mini-month")[0].GetAttribute("data-month"));

        rail.Render(parameters => parameters.Add(component => component.Shown, new DateOnly(2027, 6, 2)));
        Assert.Equal("2027-06-01", rail.FindAll("table.mini-month")[0].GetAttribute("data-month"));
    }

    [Fact]
    public void A_public_holiday_is_marked_in_the_holiday_colour_and_named_in_its_days_label_and_title_beside_a_closed_day_of_the_tenant()
    {
        var christmas = new DateOnly(2026, 12, 25);
        var founders = new ClosedDayFact(Guid.NewGuid(), "Founders' Day", christmas, christmas, null);
        var rail = Render<CalendarRail>(parameters => parameters
            .Add(component => component.Today, Today)
            .Add(component => component.Shown, Today)
            .Add(component => component.Closed, (from, to) => ClosedDayRules.MarkDays([founders], from, to))
            .Add(component => component.Holidays, new TenantHolidays(HolidayCalendar.Sweden, false)));

        var eve = rail.Find("button.mini-month__day[data-day='2026-12-24']");
        Assert.Contains("mini-month__day--holiday", eve.ClassName, StringComparison.Ordinal);
        Assert.Equal(("Christmas Eve", "Thursday 24 December 2026, Christmas Eve", "Christmas Eve"), (eve.GetAttribute("data-holiday"), eve.GetAttribute("aria-label"), eve.GetAttribute("title")));
        var both = rail.Find("button.mini-month__day[data-day='2026-12-25']");
        Assert.Contains("mini-month__day--holiday", both.ClassName, StringComparison.Ordinal);
        Assert.Contains("mini-month__day--closed", both.ClassName, StringComparison.Ordinal);
        Assert.Equal("Friday 25 December 2026, Christmas Day, Closed for Founders' Day", both.GetAttribute("aria-label"));
        Assert.Equal("Christmas Day, Closed for Founders' Day", both.GetAttribute("title"));
        Assert.Equal(["2026-12-24", "2026-12-25", "2026-12-26", "2026-12-31", "2027-01-01", "2027-01-06"], rail.FindAll("button.mini-month__day--holiday").Select(day => day.GetAttribute("data-day")));
        Assert.DoesNotContain("mini-month__day--holiday", rail.Find("button.mini-month__day[data-day='2026-12-23']").ClassName, StringComparison.Ordinal);
        Assert.Contains("Public holiday", rail.Find(".calendar-rail__legend").TextContent, StringComparison.Ordinal);

        rail.Render(parameters => parameters.Add(component => component.Holidays, new TenantHolidays(HolidayCalendar.Sweden, true)));
        Assert.Equal("Thursday 24 December 2026, Closed for Christmas Eve", rail.Find("button.mini-month__day[data-day='2026-12-24']").GetAttribute("aria-label"));

        rail.Render(parameters => parameters.Add(component => component.Holidays, TenantHolidays.None));
        Assert.Empty(rail.FindAll("button.mini-month__day--holiday"));
        Assert.DoesNotContain("Public holiday", rail.Find(".calendar-rail__legend").TextContent, StringComparison.Ordinal);
    }
}
