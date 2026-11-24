using System.Globalization;

namespace Staffora.Core.Staffing;

public sealed record ResponseWindow
{
    public const int DefaultHours = 48;

    public const int MaxHours = 720;

    private static readonly int[] offeredHours = [4, 12, 24, 48, 72, 168];

    private ResponseWindow(int? hours)
    {
        Hours = hours;
    }

    public static ResponseWindow None { get; } = new((int?)null);

    public static ResponseWindow Default { get; } = new(DefaultHours);

    public int? Hours { get; }

    public string Label => Hours switch
    {
        null => "No deadline",
        1 => "1 hour",
        168 => "1 week",
        24 => "1 day",
        { } hours when hours % 24 == 0 => $"{hours / 24} days",
        { } hours => $"{hours} hours",
    };

    public static ResponseWindow OfHours(int hours) =>
        hours is >= 1 and <= MaxHours ? new ResponseWindow(hours) : throw new ArgumentOutOfRangeException(nameof(hours), hours, $"A time to answer is 1 to {MaxHours} hours.");

    public static IReadOnlyList<ResponseWindow> Choices(ResponseWindow tenantDefault)
    {
        ArgumentNullException.ThrowIfNull(tenantDefault);
        return [.. offeredHours.Concat(tenantDefault.Hours is { } hours ? [hours] : []).Distinct().Order().Select(OfHours), None];
    }

    public static bool TryParse(string text, out ResponseWindow window)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            window = None;
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours is >= 1 and <= MaxHours && text == hours.ToString(CultureInfo.InvariantCulture))
        {
            window = new ResponseWindow(hours);
            return true;
        }

        window = None;
        return false;
    }

    public static string InvalidReason(string text) => $"\"{text}\" is not a whole number of hours from 1 to {MaxHours}. Leave it empty for no deadline.";

    public string Format() => Hours?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public DateTime? DeadlineAfter(DateTime sentUtc) => Hours is { } hours ? sentUtc.AddHours(hours) : null;

    public static string AnswerByText(DateTimeOffset local) => $"Answer by {local.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}";
}
