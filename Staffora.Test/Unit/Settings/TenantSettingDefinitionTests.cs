using Staffora.Core.Scheduling;
using Staffora.Core.Settings;
using Staffora.Core.Staffing;
using Staffora.Services.Settings;

namespace Staffora.Test.Unit.Settings;

public class TenantSettingDefinitionTests
{
    [Fact]
    public void Every_setting_has_a_unique_key_that_fits_its_column_and_is_found_by_it()
    {
        var keys = TenantSettingDefinitions.All.Select(definition => definition.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.InRange(key.Length, 1, TenantSettingDefinition.KeyMaxLength));
        Assert.All(TenantSettingDefinitions.All, definition => Assert.Same(definition, TenantSettingDefinitions.Find(definition.Key)));
        Assert.Null(TenantSettingDefinitions.Find("general.timezone"));
        Assert.Null(TenantSettingDefinitions.Find(null));
    }

    [Fact]
    public void The_time_zone_is_a_general_setting_whose_default_is_the_platforms_zone()
    {
        var timeZone = TenantSettingDefinitions.TimeZone;
        var defaults = new PlatformOptions { TimeZone = "Asia/Tokyo" }.Defaults;

        Assert.Equal(("General.TimeZone", TenantSettingCategory.General, TenantSettingKind.TimeZone, "Time zone"), (timeZone.Key, timeZone.Category, timeZone.Kind, timeZone.DisplayName));
        Assert.Equal("Asia/Tokyo", timeZone.DefaultValue(defaults).Id);
        Assert.Equal("Asia/Tokyo", timeZone.DefaultText(defaults));
        Assert.Equal(IanaTimeZones.Utc, new PlatformOptions().TimeZone);
    }

    [Theory]
    [InlineData("Europe/Stockholm")]
    [InlineData("America/New_York")]
    [InlineData("Asia/Kolkata")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Etc/UTC")]
    public void An_offered_IANA_id_is_accepted_as_it_is_spelled(string id)
    {
        var timeZone = TenantSettingDefinitions.TimeZone;

        Assert.Null(timeZone.DescribeInvalid(id));
        Assert.True(timeZone.TryParse(id, out var zone));
        Assert.Equal(id, timeZone.Format(zone));
        Assert.Contains(id, IanaTimeZones.Ids);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("europe/stockholm")]
    [InlineData("Asia/Calcutta")]
    [InlineData("Etc/GMT+1")]
    [InlineData(" Europe/Stockholm")]
    public void Anything_else_is_refused_with_its_reason(string text)
    {
        var timeZone = TenantSettingDefinitions.TimeZone;

        Assert.Equal($"\"{text}\" is not a time zone this system offers. Choose one from the list, such as Europe/Stockholm.", timeZone.DescribeInvalid(text));
        Assert.False(timeZone.TryParse(text, out _));
        Assert.False(IanaTimeZones.IsKnown(text));
    }

    [Fact]
    public void An_empty_or_overlong_value_is_refused_with_its_reason()
    {
        var timeZone = TenantSettingDefinitions.TimeZone;

        Assert.Equal("Time zone needs a value.", timeZone.DescribeInvalid(null));
        Assert.Equal("Time zone needs a value.", timeZone.DescribeInvalid("  "));
        Assert.Equal("Time zone is at most 400 characters.", timeZone.DescribeInvalid(new string('x', 401)));
    }

    [Fact]
    public void The_picker_names_each_zone_by_its_offset_at_the_instant_and_orders_them_by_it()
    {
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("(UTC+01:00) Europe/Stockholm", IanaTimeZones.Label("Europe/Stockholm", winter));
        Assert.Equal("(UTC+02:00) Europe/Stockholm", IanaTimeZones.Label("Europe/Stockholm", summer));
        Assert.Equal("(UTC-05:00) America/New York", IanaTimeZones.Label("America/New_York", winter));
        Assert.Equal("(UTC+05:30) Asia/Kolkata", IanaTimeZones.Label("Asia/Kolkata", winter));
        Assert.Equal("(UTC+00:00) Coordinated Universal Time (Etc/UTC)", IanaTimeZones.Label(IanaTimeZones.Utc, winter));

        var options = IanaTimeZones.Options(winter);
        Assert.Equal(IanaTimeZones.Ids.Order(StringComparer.Ordinal), options.Select(option => option.Id).Order(StringComparer.Ordinal));
        var offsets = options.Select(option => IanaTimeZones.Find(option.Id).GetUtcOffset(winter)).ToList();
        Assert.Equal(offsets.Order(), offsets);
        Assert.True(IanaTimeZones.Ids.Count > 300, $"only {IanaTimeZones.Ids.Count} zones are offered");
    }

    [Fact]
    public void A_utc_instant_converts_to_the_zones_wall_clock_across_a_daylight_saving_change()
    {
        var stockholm = IanaTimeZones.Find("Europe/Stockholm");

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 59, 0, TimeSpan.FromHours(1)), TenantClock.ToLocal(new DateTime(2026, 3, 29, 0, 59, 0, DateTimeKind.Utc), stockholm));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)), TenantClock.ToLocal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), stockholm));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(1)), TenantClock.ToLocal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), stockholm));
        Assert.Throws<ArgumentException>(() => TenantClock.ToLocal(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Local), stockholm));
        Assert.Throws<ArgumentException>(() => TenantClock.ToLocal(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Unspecified), stockholm));
    }

    [Fact]
    public void The_time_to_answer_a_request_is_48_hours_by_default_none_when_empty_and_a_whole_number_of_hours_otherwise()
    {
        var within = TenantSettingDefinitions.RespondWithin;
        var defaults = new PlatformOptions().Defaults;

        Assert.Equal(("General.RespondWithin", TenantSettingCategory.General, TenantSettingKind.Hours, true, false), (within.Key, within.Category, within.Kind, within.AllowsEmpty, within.IsPinned));
        Assert.True(TenantSettingDefinitions.TimeZone.IsPinned);
        Assert.Equal((48, "48", "2 days"), (within.DefaultValue(defaults).Hours, within.DefaultText(defaults), within.DefaultValue(defaults).Label));

        Assert.Null(within.DescribeInvalid(string.Empty));
        Assert.Null(within.DescribeInvalid(null));
        Assert.True(within.TryParse(string.Empty, out var none));
        Assert.Same(ResponseWindow.None, none);
        Assert.Equal(("No deadline", string.Empty, (DateTime?)null), (none.Label, within.Format(none), none.DeadlineAfter(DateTime.UtcNow)));

        Assert.True(within.TryParse("12", out var twelve));
        Assert.Equal((12, "12 hours"), (twelve.Hours, twelve.Label));
        Assert.Equal(new DateTime(2026, 11, 24, 21, 0, 0, DateTimeKind.Utc), twelve.DeadlineAfter(new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(["1 hour", "1 day", "3 days", "1 week", "30 days"], new[] { 1, 24, 72, 168, 720 }.Select(hours => ResponseWindow.OfHours(hours).Label));

        foreach (var text in new[] { "0", "721", "-4", "1.5", "two", " 12", "012", "+12" })
        {
            Assert.False(within.TryParse(text, out _), text);
            Assert.Equal($"\"{text}\" is not a whole number of hours from 1 to 720. Leave it empty for no deadline.", within.DescribeInvalid(text));
        }

        Assert.Equal([4, 12, 24, 36, 48, 72, 168, null], ResponseWindow.Choices(ResponseWindow.OfHours(36)).Select(choice => choice.Hours));
        Assert.Equal([4, 12, 24, 48, 72, 168, null], ResponseWindow.Choices(ResponseWindow.None).Select(choice => choice.Hours));
        Assert.Equal("Answer by Tue 24 Nov 2026, 14:00", ResponseWindow.AnswerByText(new DateTimeOffset(2026, 11, 24, 14, 0, 0, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void The_public_holiday_calendar_is_Sweden_by_default_none_when_empty_and_the_tenant_is_open_on_holidays_by_default()
    {
        var calendar = TenantSettingDefinitions.PublicHolidays;
        var closed = TenantSettingDefinitions.ClosedOnPublicHolidays;
        var defaults = new PlatformOptions().Defaults;

        Assert.Equal(("General.PublicHolidays", TenantSettingCategory.General, TenantSettingKind.HolidayCalendar, true, false), (calendar.Key, calendar.Category, calendar.Kind, calendar.AllowsEmpty, calendar.IsPinned));
        Assert.Equal(("General.ClosedOnPublicHolidays", TenantSettingCategory.General, TenantSettingKind.Switch, false, false), (closed.Key, closed.Category, closed.Kind, closed.AllowsEmpty, closed.IsPinned));
        Assert.Equal((HolidayCalendar.Sweden, "SE"), (calendar.DefaultValue(defaults), calendar.DefaultText(defaults)));
        Assert.Equal((false, "false"), (closed.DefaultValue(defaults), closed.DefaultText(defaults)));

        Assert.True(calendar.TryParse(string.Empty, out var none));
        Assert.True(none.IsNone);
        Assert.Null(calendar.DescribeInvalid(null));
        Assert.Equal("\"DK\" is not a public holiday calendar this system offers. Choose SE for Sweden, or leave it empty for none.", calendar.DescribeInvalid("DK"));

        Assert.True(closed.TryParse("true", out var on));
        Assert.True(on);
        Assert.True(closed.TryParse("False", out var off));
        Assert.False(off);
        Assert.Equal("Closed on public holidays needs a value.", closed.DescribeInvalid(string.Empty));
        Assert.Equal("\"yes\" is not on or off. Use true or false.", closed.DescribeInvalid("yes"));
        Assert.Same(calendar, TenantSettingDefinitions.Find("General.PublicHolidays"));
        Assert.Same(closed, TenantSettingDefinitions.Find("General.ClosedOnPublicHolidays"));
    }
}
