using Staffora.Core.Scheduling;

namespace Staffora.Test.Unit.Scheduling;

public class PublicHolidaysTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static readonly Guid Ada = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2028, 4, 16)]
    [InlineData(2029, 4, 1)]
    [InlineData(2030, 4, 21)]
    public void Easter_Sunday_is_the_date_of_the_anonymous_Gregorian_computus_and_the_movable_holidays_follow_it(int year, int month, int day)
    {
        var easter = new DateOnly(year, month, day);
        var holidays = Swedish(year);

        Assert.Equal(easter, PublicHolidays.EasterSunday(year));
        Assert.Equal(easter.AddDays(-2), holidays["Good Friday"]);
        Assert.Equal(easter, holidays["Easter Sunday"]);
        Assert.Equal(easter.AddDays(1), holidays["Easter Monday"]);
        Assert.Equal(easter.AddDays(39), holidays["Ascension Day"]);
        Assert.Equal(DayOfWeek.Thursday, holidays["Ascension Day"].DayOfWeek);
        Assert.Equal(easter.AddDays(49), holidays["Whit Sunday"]);
    }

    [Theory]
    [InlineData(2024, 22, 2)]
    [InlineData(2025, 21, 1)]
    [InlineData(2026, 20, 31)]
    [InlineData(2027, 26, 6)]
    [InlineData(2028, 24, 4)]
    public void Midsummer_Day_and_All_Saints_Day_fall_on_the_named_Saturdays(int year, int midsummerDay, int allSaintsDay)
    {
        var holidays = Swedish(year);

        Assert.Equal(new DateOnly(year, 6, midsummerDay), holidays["Midsummer Day"]);
        Assert.Equal(new DateOnly(year, allSaintsDay == 31 ? 10 : 11, allSaintsDay), holidays["All Saints' Day"]);
    }

    [Fact]
    public void Every_year_Midsummer_Day_is_the_Saturday_between_20_and_26_June_and_All_Saints_Day_the_Saturday_between_31_October_and_6_November()
    {
        for (var year = 2000; year <= 2100; year++)
        {
            var holidays = Swedish(year);
            var midsummer = holidays["Midsummer Day"];
            var allSaints = holidays["All Saints' Day"];

            Assert.Equal(DayOfWeek.Saturday, midsummer.DayOfWeek);
            Assert.InRange(midsummer, new DateOnly(year, 6, 20), new DateOnly(year, 6, 26));
            Assert.Equal(midsummer.AddDays(-1), holidays["Midsummer Eve"]);
            Assert.Equal(DayOfWeek.Saturday, allSaints.DayOfWeek);
            Assert.InRange(allSaints, new DateOnly(year, 10, 31), new DateOnly(year, 11, 6));
        }
    }

    [Fact]
    public void A_Swedish_year_names_the_fixed_holidays_and_the_three_eves_most_people_have_off_in_date_order()
    {
        var year = PublicHolidays.OfYear(HolidayCalendar.Sweden, 2026);

        Assert.Equal(
            [
                ("2026-01-01", "New Year's Day", PublicHolidayKind.Holiday),
                ("2026-01-06", "Epiphany", PublicHolidayKind.Holiday),
                ("2026-04-03", "Good Friday", PublicHolidayKind.Holiday),
                ("2026-04-05", "Easter Sunday", PublicHolidayKind.Holiday),
                ("2026-04-06", "Easter Monday", PublicHolidayKind.Holiday),
                ("2026-05-01", "May Day", PublicHolidayKind.Holiday),
                ("2026-05-14", "Ascension Day", PublicHolidayKind.Holiday),
                ("2026-05-24", "Whit Sunday", PublicHolidayKind.Holiday),
                ("2026-06-06", "National Day", PublicHolidayKind.Holiday),
                ("2026-06-19", "Midsummer Eve", PublicHolidayKind.Eve),
                ("2026-06-20", "Midsummer Day", PublicHolidayKind.Holiday),
                ("2026-10-31", "All Saints' Day", PublicHolidayKind.Holiday),
                ("2026-12-24", "Christmas Eve", PublicHolidayKind.Eve),
                ("2026-12-25", "Christmas Day", PublicHolidayKind.Holiday),
                ("2026-12-26", "Boxing Day", PublicHolidayKind.Holiday),
                ("2026-12-31", "New Year's Eve", PublicHolidayKind.Eve),
            ],
            year.Select(holiday => (ScheduleRules.DateText(holiday.Date), holiday.Name, holiday.Kind)));
        Assert.Equal(["Midsummer Eve", "Christmas Eve", "New Year's Eve"], year.Where(holiday => holiday.Kind == PublicHolidayKind.Eve).Select(holiday => holiday.Name));
        Assert.Equal(year.Count, year.Select(holiday => holiday.Id).Distinct().Count());
        Assert.Equal(year[0].Id, PublicHolidays.OfYear(HolidayCalendar.Sweden, 2026)[0].Id);
    }

    [Fact]
    public void A_range_across_the_turn_of_the_year_answers_the_days_of_both_years_and_no_calendar_answers_none()
    {
        var range = PublicHolidays.Of(HolidayCalendar.Sweden, new DateOnly(2026, 12, 24), new DateOnly(2027, 1, 6));

        Assert.Equal(["Christmas Eve", "Christmas Day", "Boxing Day", "New Year's Eve", "New Year's Day", "Epiphany"], range.Select(holiday => holiday.Name));
        Assert.Empty(PublicHolidays.Of(HolidayCalendar.None, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Empty(PublicHolidays.Of(HolidayCalendar.Sweden, new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 30)));
        Assert.Equal(["Ascension Day", "May Day"], PublicHolidays.Of(HolidayCalendar.Sweden, new DateOnly(2008, 5, 1), new DateOnly(2008, 5, 1)).Select(holiday => holiday.Name));
    }

    [Fact]
    public void The_calendar_setting_reads_SE_as_Sweden_and_empty_as_none_and_refuses_anything_else()
    {
        Assert.True(HolidayCalendar.TryParse("SE", out var sweden));
        Assert.Same(HolidayCalendar.Sweden, sweden);
        Assert.True(HolidayCalendar.TryParse(string.Empty, out var none));
        Assert.True(none.IsNone);
        Assert.False(HolidayCalendar.TryParse("se", out _));
        Assert.False(HolidayCalendar.TryParse("NO", out _));
        Assert.Equal(["Sweden", "None"], HolidayCalendar.Offered.Select(calendar => calendar.Name));
    }

    [Fact]
    public void Only_a_tenant_closed_on_its_holidays_turns_them_into_closed_days_and_names_them_closed()
    {
        var christmas = new DateOnly(2026, 12, 25);
        var marked = new TenantHolidays(HolidayCalendar.Sweden, false);
        var closed = new TenantHolidays(HolidayCalendar.Sweden, true);

        Assert.Empty(marked.ClosedDays(christmas, christmas));
        Assert.Empty(new TenantHolidays(HolidayCalendar.None, true).ClosedDays(christmas, christmas));
        Assert.Equal("Christmas Day", marked.TextOf(marked.ByDay(christmas, christmas)[christmas]));
        Assert.Equal("Closed for Christmas Day", closed.TextOf(closed.ByDay(christmas, christmas)[christmas]));
        var day = Assert.Single(closed.ClosedDays(christmas, christmas));
        Assert.Equal(("Christmas Day", christmas, christmas, (IReadOnlySet<Guid>?)null), (day.Name, day.FirstDay, day.LastDay, day.Units));
    }

    [Fact]
    public void A_holiday_of_a_tenant_closed_on_its_holidays_blocks_every_staff_member_from_midnight_to_midnight_like_a_closed_day_and_an_eve_too()
    {
        var eve = new DateOnly(2026, 12, 24);
        var holidays = new TenantHolidays(HolidayCalendar.Sweden, true);

        var answer = AvailabilityRules.Compute([new ScheduledStaff(Ada, true)], [], new Dictionary<Guid, PatternShape>(), [], [], holidays.ClosedDays(eve, eve.AddDays(1)), eve, eve.AddDays(1), Stockholm);

        Assert.Equal(
            [("2026-12-23T23:00:00Z", "Closed for Christmas Eve"), ("2026-12-24T23:00:00Z", "Closed for Christmas Day")],
            answer[Ada].Blocks.Select(block => (block.StartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture), block.Reason)));
        Assert.Equal(["Christmas Eve", "Christmas Day"], answer[Ada].Closed.Select(closed => closed.Name));
    }

    private static Dictionary<string, DateOnly> Swedish(int year) =>
        PublicHolidays.OfYear(HolidayCalendar.Sweden, year).ToDictionary(holiday => holiday.Name, holiday => holiday.Date);
}
