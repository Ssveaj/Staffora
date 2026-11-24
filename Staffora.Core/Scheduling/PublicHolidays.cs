namespace Staffora.Core.Scheduling;

public enum PublicHolidayKind
{
    Holiday = 1,

    Eve = 2,
}

public sealed record PublicHoliday(DateOnly Date, string Name, PublicHolidayKind Kind)
{
    public Guid Id { get; } = PublicHolidays.IdOf(Date, Name);
}

public sealed record HolidayCalendar
{
    private HolidayCalendar(string code, string name)
    {
        Code = code;
        Name = name;
    }

    public static HolidayCalendar None { get; } = new(string.Empty, "None");

    public static HolidayCalendar Sweden { get; } = new("SE", "Sweden");

    public static IReadOnlyList<HolidayCalendar> Offered { get; } = [Sweden, None];

    public static HolidayCalendar Default => Sweden;

    public string Code { get; }

    public string Name { get; }

    public bool IsNone => Code.Length == 0;

    public static bool TryParse(string text, out HolidayCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = Offered.FirstOrDefault(offered => string.Equals(offered.Code, text, StringComparison.Ordinal));
        calendar = found ?? None;
        return found is not null;
    }

    public static string InvalidReason(string text) => $"\"{text}\" is not a public holiday calendar this system offers. Choose SE for Sweden, or leave it empty for none.";

    public string Format() => Code;
}

public sealed record TenantHolidays(HolidayCalendar Calendar, bool ClosedOnHolidays)
{
    public static TenantHolidays None { get; } = new(HolidayCalendar.None, false);

    public bool Blocks => ClosedOnHolidays && !Calendar.IsNone;

    public IReadOnlyList<PublicHoliday> Between(DateOnly from, DateOnly to) => PublicHolidays.Of(Calendar, from, to);

    public IReadOnlyDictionary<DateOnly, IReadOnlyList<PublicHoliday>> ByDay(DateOnly from, DateOnly to) =>
        Between(from, to).GroupBy(holiday => holiday.Date).ToDictionary(day => day.Key, day => (IReadOnlyList<PublicHoliday>)[.. day]);

    public IReadOnlyList<ScheduledClosedDay> ClosedDays(DateOnly from, DateOnly to) =>
        Blocks ? [.. Between(from, to).Select(holiday => new ScheduledClosedDay(holiday.Id, holiday.Name, holiday.Date, holiday.Date, null))] : [];

    public string TextOf(PublicHoliday holiday)
    {
        ArgumentNullException.ThrowIfNull(holiday);
        return Blocks ? ClosedDayRules.BlockReason(holiday.Name) : holiday.Name;
    }

    public string TextOf(IEnumerable<PublicHoliday> holidays)
    {
        ArgumentNullException.ThrowIfNull(holidays);
        return string.Join(", ", holidays.Select(TextOf));
    }
}

public static class PublicHolidays
{
    public static IReadOnlyList<PublicHoliday> Of(HolidayCalendar calendar, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (calendar.IsNone || to < from)
        {
            return [];
        }

        var days = new List<PublicHoliday>();
        for (var year = from.Year; year <= to.Year; year++)
        {
            days.AddRange(OfYear(calendar, year).Where(holiday => from <= holiday.Date && holiday.Date <= to));
        }

        return days;
    }

    public static IReadOnlyList<PublicHoliday> OfYear(HolidayCalendar calendar, int year)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        return calendar == HolidayCalendar.Sweden ? Swedish(year) : [];
    }

    public static DateOnly EasterSunday(int year)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        var golden = year % 19;
        var century = year / 100;
        var yearOfCentury = year % 100;
        var leapCenturies = century / 4;
        var centuryRemainder = century % 4;
        var moonCorrection = (century + 8) / 25;
        var solarCorrection = (century - moonCorrection + 1) / 3;
        var epact = ((19 * golden) + century - leapCenturies - solarCorrection + 15) % 30;
        var leapYears = yearOfCentury / 4;
        var yearRemainder = yearOfCentury % 4;
        var weekday = (32 + (2 * centuryRemainder) + (2 * leapYears) - epact - yearRemainder) % 7;
        var shift = (golden + (11 * epact) + (22 * weekday)) / 451;
        var count = epact + weekday - (7 * shift) + 114;
        return new DateOnly(year, count / 31, (count % 31) + 1);
    }

    public static Guid IdOf(DateOnly date, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var named = 0;
        foreach (var letter in name)
        {
            named = unchecked((named * 31) + letter);
        }

        return new Guid(date.DayNumber, 0x0907, unchecked((short)named), 0x68, 0x6f, 0x6c, 0x69, 0x64, 0x61, 0x79, 0x73);
    }

    private static List<PublicHoliday> Swedish(int year)
    {
        var easter = EasterSunday(year);
        var midsummer = SaturdayFrom(new DateOnly(year, 6, 20));
        var allSaints = SaturdayFrom(new DateOnly(year, 10, 31));
        List<PublicHoliday> days =
        [
            Holiday(new DateOnly(year, 1, 1), "New Year's Day"),
            Holiday(new DateOnly(year, 1, 6), "Epiphany"),
            Holiday(easter.AddDays(-2), "Good Friday"),
            Holiday(easter, "Easter Sunday"),
            Holiday(easter.AddDays(1), "Easter Monday"),
            Holiday(new DateOnly(year, 5, 1), "May Day"),
            Holiday(easter.AddDays(39), "Ascension Day"),
            Holiday(easter.AddDays(49), "Whit Sunday"),
            Holiday(new DateOnly(year, 6, 6), "National Day"),
            Eve(midsummer.AddDays(-1), "Midsummer Eve"),
            Holiday(midsummer, "Midsummer Day"),
            Holiday(allSaints, "All Saints' Day"),
            Eve(new DateOnly(year, 12, 24), "Christmas Eve"),
            Holiday(new DateOnly(year, 12, 25), "Christmas Day"),
            Holiday(new DateOnly(year, 12, 26), "Boxing Day"),
            Eve(new DateOnly(year, 12, 31), "New Year's Eve"),
        ];
        return [.. days.OrderBy(day => day.Date).ThenBy(day => day.Kind).ThenBy(day => day.Name, StringComparer.Ordinal)];
    }

    private static DateOnly SaturdayFrom(DateOnly first) => first.AddDays(((int)DayOfWeek.Saturday - (int)first.DayOfWeek + 7) % 7);

    private static PublicHoliday Holiday(DateOnly date, string name) => new(date, name, PublicHolidayKind.Holiday);

    private static PublicHoliday Eve(DateOnly date, string name) => new(date, name, PublicHolidayKind.Eve);
}
