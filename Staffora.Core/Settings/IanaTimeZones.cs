using System.Globalization;

namespace Staffora.Core.Settings;

public static class IanaTimeZones
{
    public const string Utc = "Etc/UTC";

    private static readonly string[] listed =
    [
        "Africa/Abidjan", "Africa/Accra", "Africa/Addis_Ababa", "Africa/Algiers", "Africa/Asmara", "Africa/Bamako", "Africa/Bangui",
        "Africa/Banjul", "Africa/Bissau", "Africa/Blantyre", "Africa/Brazzaville", "Africa/Bujumbura", "Africa/Cairo", "Africa/Casablanca",
        "Africa/Conakry", "Africa/Dakar", "Africa/Dar_es_Salaam", "Africa/Djibouti", "Africa/Douala", "Africa/Freetown", "Africa/Gaborone",
        "Africa/Harare", "Africa/Johannesburg", "Africa/Juba", "Africa/Kampala", "Africa/Khartoum", "Africa/Kigali", "Africa/Kinshasa",
        "Africa/Lagos", "Africa/Libreville", "Africa/Lome", "Africa/Luanda", "Africa/Lubumbashi", "Africa/Lusaka", "Africa/Malabo",
        "Africa/Maputo", "Africa/Maseru", "Africa/Mbabane", "Africa/Mogadishu", "Africa/Monrovia", "Africa/Nairobi", "Africa/Ndjamena",
        "Africa/Niamey", "Africa/Nouakchott", "Africa/Ouagadougou", "Africa/Porto-Novo", "Africa/Sao_Tome", "Africa/Tripoli", "Africa/Tunis",
        "Africa/Windhoek",
        "America/Adak", "America/Anchorage", "America/Anguilla", "America/Antigua", "America/Araguaina", "America/Argentina/Buenos_Aires",
        "America/Aruba", "America/Asuncion", "America/Atikokan", "America/Bahia", "America/Barbados", "America/Belize", "America/Blanc-Sablon",
        "America/Bogota", "America/Cancun", "America/Caracas", "America/Cayenne", "America/Cayman", "America/Chicago", "America/Chihuahua",
        "America/Costa_Rica", "America/Creston", "America/Cuiaba", "America/Curacao", "America/Danmarkshavn", "America/Denver",
        "America/Dominica", "America/Edmonton", "America/El_Salvador", "America/Fortaleza", "America/Grand_Turk", "America/Grenada",
        "America/Guadeloupe", "America/Guatemala", "America/Guayaquil", "America/Guyana", "America/Halifax", "America/Havana",
        "America/Hermosillo", "America/Indiana/Indianapolis", "America/Jamaica", "America/Kralendijk", "America/La_Paz", "America/Lima",
        "America/Los_Angeles", "America/Lower_Princes", "America/Managua", "America/Manaus", "America/Marigot", "America/Martinique",
        "America/Matamoros", "America/Mexico_City", "America/Miquelon", "America/Montevideo", "America/Montserrat", "America/Nassau",
        "America/New_York", "America/Noronha", "America/Nuuk", "America/Ojinaga", "America/Panama", "America/Paramaribo", "America/Phoenix",
        "America/Port-au-Prince", "America/Port_of_Spain", "America/Puerto_Rico", "America/Punta_Arenas", "America/Regina",
        "America/Rio_Branco", "America/Santiago", "America/Santo_Domingo", "America/Sao_Paulo", "America/Scoresbysund",
        "America/St_Barthelemy", "America/St_Johns", "America/St_Kitts", "America/St_Lucia", "America/St_Thomas", "America/St_Vincent",
        "America/Tegucigalpa", "America/Thule", "America/Tijuana", "America/Toronto", "America/Tortola", "America/Vancouver",
        "America/Whitehorse", "America/Winnipeg",
        "Arctic/Longyearbyen",
        "Asia/Aden", "Asia/Almaty", "Asia/Amman", "Asia/Ashgabat", "Asia/Baghdad", "Asia/Bahrain", "Asia/Baku", "Asia/Bangkok", "Asia/Barnaul",
        "Asia/Beirut", "Asia/Bishkek", "Asia/Brunei", "Asia/Chita", "Asia/Colombo", "Asia/Damascus", "Asia/Dhaka", "Asia/Dili", "Asia/Dubai",
        "Asia/Dushanbe", "Asia/Hebron", "Asia/Ho_Chi_Minh", "Asia/Hong_Kong", "Asia/Hovd", "Asia/Irkutsk", "Asia/Jakarta", "Asia/Jayapura",
        "Asia/Jerusalem", "Asia/Kabul", "Asia/Kamchatka", "Asia/Karachi", "Asia/Kathmandu", "Asia/Kolkata", "Asia/Krasnoyarsk",
        "Asia/Kuala_Lumpur", "Asia/Kuwait", "Asia/Macau", "Asia/Magadan", "Asia/Makassar", "Asia/Manila", "Asia/Muscat", "Asia/Nicosia",
        "Asia/Novosibirsk", "Asia/Omsk", "Asia/Oral", "Asia/Phnom_Penh", "Asia/Pyongyang", "Asia/Qatar", "Asia/Qyzylorda", "Asia/Riyadh",
        "Asia/Sakhalin", "Asia/Seoul", "Asia/Shanghai", "Asia/Singapore", "Asia/Srednekolymsk", "Asia/Taipei", "Asia/Tashkent", "Asia/Tbilisi",
        "Asia/Tehran", "Asia/Thimphu", "Asia/Tokyo", "Asia/Tomsk", "Asia/Ulaanbaatar", "Asia/Vientiane", "Asia/Vladivostok", "Asia/Yakutsk",
        "Asia/Yangon", "Asia/Yekaterinburg", "Asia/Yerevan",
        "Atlantic/Azores", "Atlantic/Bermuda", "Atlantic/Canary", "Atlantic/Cape_Verde", "Atlantic/Faroe", "Atlantic/Reykjavik",
        "Atlantic/St_Helena", "Atlantic/Stanley",
        "Australia/Adelaide", "Australia/Brisbane", "Australia/Darwin", "Australia/Eucla", "Australia/Hobart", "Australia/Lord_Howe",
        "Australia/Perth", "Australia/Sydney",
        Utc,
        "Europe/Amsterdam", "Europe/Andorra", "Europe/Astrakhan", "Europe/Athens", "Europe/Belgrade", "Europe/Berlin", "Europe/Bratislava",
        "Europe/Brussels", "Europe/Bucharest", "Europe/Budapest", "Europe/Chisinau", "Europe/Copenhagen", "Europe/Dublin", "Europe/Gibraltar",
        "Europe/Guernsey", "Europe/Helsinki", "Europe/Isle_of_Man", "Europe/Istanbul", "Europe/Jersey", "Europe/Kaliningrad", "Europe/Kyiv",
        "Europe/Lisbon", "Europe/Ljubljana", "Europe/London", "Europe/Luxembourg", "Europe/Madrid", "Europe/Malta", "Europe/Mariehamn",
        "Europe/Minsk", "Europe/Monaco", "Europe/Moscow", "Europe/Oslo", "Europe/Paris", "Europe/Podgorica", "Europe/Prague", "Europe/Riga",
        "Europe/Rome", "Europe/Samara", "Europe/San_Marino", "Europe/Sarajevo", "Europe/Saratov", "Europe/Simferopol", "Europe/Skopje",
        "Europe/Sofia", "Europe/Stockholm", "Europe/Tallinn", "Europe/Tirane", "Europe/Vaduz", "Europe/Vatican", "Europe/Vienna",
        "Europe/Vilnius", "Europe/Volgograd", "Europe/Warsaw", "Europe/Zagreb", "Europe/Zurich",
        "Indian/Antananarivo", "Indian/Chagos", "Indian/Christmas", "Indian/Cocos", "Indian/Comoro", "Indian/Mahe", "Indian/Maldives",
        "Indian/Mauritius", "Indian/Mayotte", "Indian/Reunion",
        "Pacific/Apia", "Pacific/Auckland", "Pacific/Bougainville", "Pacific/Chatham", "Pacific/Chuuk", "Pacific/Easter", "Pacific/Efate",
        "Pacific/Fakaofo", "Pacific/Fiji", "Pacific/Funafuti", "Pacific/Galapagos", "Pacific/Gambier", "Pacific/Guadalcanal", "Pacific/Guam",
        "Pacific/Honolulu", "Pacific/Kanton", "Pacific/Kiritimati", "Pacific/Majuro", "Pacific/Marquesas", "Pacific/Midway", "Pacific/Nauru",
        "Pacific/Niue", "Pacific/Norfolk", "Pacific/Noumea", "Pacific/Pago_Pago", "Pacific/Palau", "Pacific/Pitcairn", "Pacific/Pohnpei",
        "Pacific/Port_Moresby", "Pacific/Rarotonga", "Pacific/Saipan", "Pacific/Tahiti", "Pacific/Tarawa", "Pacific/Tongatapu",
        "Pacific/Wake", "Pacific/Wallis",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, TimeZoneInfo>> resolved = new(() =>
        listed.Select(id => (Id: id, Zone: TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null))
            .Where(entry => entry.Zone is not null)
            .ToDictionary(entry => entry.Id, entry => entry.Zone!, StringComparer.Ordinal));

    public static IReadOnlyList<string> Ids { get; } = [.. listed.Where(id => resolved.Value.ContainsKey(id))];

    public static bool IsKnown(string? id) => id is not null && resolved.Value.ContainsKey(id);

    public static bool TryFind(string? id, out TimeZoneInfo zone)
    {
        if (id is not null && resolved.Value.TryGetValue(id, out var found))
        {
            zone = found;
            return true;
        }

        zone = TimeZoneInfo.Utc;
        return false;
    }

    public static TimeZoneInfo Find(string id) =>
        TryFind(id, out var zone) ? zone : throw new ArgumentException($"\"{id}\" is not one of the IANA time zones this system offers.", nameof(id));

    public static string Label(string id, DateTimeOffset instant)
    {
        var zone = Find(id);
        var offset = zone.GetUtcOffset(instant);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var text = string.Create(CultureInfo.InvariantCulture, $"(UTC{sign}{offset.Duration():hh\\:mm})");
        return id == Utc ? $"{text} Coordinated Universal Time ({Utc})" : $"{text} {id.Replace('_', ' ')}";
    }

    public static IReadOnlyList<(string Id, string Label)> Options(DateTimeOffset instant) =>
        [.. Ids.OrderBy(id => resolved.Value[id].GetUtcOffset(instant)).ThenBy(id => id, StringComparer.Ordinal).Select(id => (id, Label(id, instant)))];
}
