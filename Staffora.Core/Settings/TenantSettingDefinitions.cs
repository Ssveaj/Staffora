using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Core.Settings;

public static class TenantSettingDefinitions
{
    public static TenantSettingDefinition<TimeZoneInfo> TimeZone { get; } = new(
        key: "General.TimeZone",
        category: TenantSettingCategory.General,
        kind: TenantSettingKind.TimeZone,
        displayName: "Time zone",
        description: "Dates and times on the company's pages are shown in this time zone, and jobs, schedules and absence are planned in it.",
        defaultValue: defaults => IanaTimeZones.Find(defaults.PlatformTimeZone),
        tryParse: IanaTimeZones.TryFind,
        format: zone => zone.Id,
        invalidReason: text => $"\"{text}\" is not a time zone this system offers. Choose one from the list, such as Europe/Stockholm.")
    {
        IsPinned = true,
    };

    public static TenantSettingDefinition<ResponseWindow> RespondWithin { get; } = new(
        key: "General.RespondWithin",
        category: TenantSettingCategory.General,
        kind: TenantSettingKind.Hours,
        displayName: "Time to answer a request",
        description: "A request to a staff member, alone or in a round, expires when nobody answers it within this many hours after it is sent, and the coordinator is told. Leave it empty for no deadline. A coordinator may choose another time when sending.",
        defaultValue: _ => ResponseWindow.Default,
        tryParse: ResponseWindow.TryParse,
        format: window => window.Format(),
        invalidReason: ResponseWindow.InvalidReason)
    {
        AllowsEmpty = true,
    };

    public static TenantSettingDefinition<HolidayCalendar> PublicHolidays { get; } = new(
        key: "General.PublicHolidays",
        category: TenantSettingCategory.General,
        kind: TenantSettingKind.HolidayCalendar,
        displayName: "Public holidays",
        description: "The calendar of public holidays and eves the calendar rail, the timeline and the staff members' schedules mark. Leave it empty for none.",
        defaultValue: _ => HolidayCalendar.Default,
        tryParse: HolidayCalendar.TryParse,
        format: calendar => calendar.Format(),
        invalidReason: HolidayCalendar.InvalidReason)
    {
        AllowsEmpty = true,
    };

    public static TenantSettingDefinition<bool> ClosedOnPublicHolidays { get; } = new(
        key: "General.ClosedOnPublicHolidays",
        category: TenantSettingCategory.General,
        kind: TenantSettingKind.Switch,
        displayName: "Closed on public holidays",
        description: "When on, every public holiday and eve of the calendar blocks staffing for the whole organization, as a closed day does.",
        defaultValue: _ => false,
        tryParse: TryParseSwitch,
        format: FormatSwitch,
        invalidReason: text => $"\"{text}\" is not on or off. Use true or false.");

    public static IReadOnlyList<TenantSettingDefinition> All { get; } = [TimeZone, RespondWithin, PublicHolidays, ClosedOnPublicHolidays];

    public static TenantSettingDefinition? Find(string? key) => All.FirstOrDefault(definition => string.Equals(definition.Key, key, StringComparison.Ordinal));

    public static string FormatSwitch(bool on) => on ? "true" : "false";

    private static bool TryParseSwitch(string text, out bool on)
    {
        on = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        return on || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
    }
}
