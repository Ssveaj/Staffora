using System.Globalization;

namespace Staffora.Blazor.Helper;

public static class ColorContrast
{
    public const double TextMinimum = 4.5;

    public const string White = "#ffffff";

    public const string Text = "#0f172a";

    public sealed record Choice(string Fill, string Foreground);

    public static Choice OnFill(string hex)
    {
        var fill = Parse(hex);
        if (Ratio(fill, Parse(White)) >= TextMinimum)
        {
            return new Choice(Format(fill), White);
        }

        if (Ratio(fill, Parse(Text)) >= TextMinimum)
        {
            return new Choice(Format(fill), Text);
        }

        for (var black = 5; black <= 100; black += 5)
        {
            var darker = Mix(fill, (0, 0, 0), black / 100.0);
            if (Ratio(darker, Parse(White)) >= TextMinimum)
            {
                return new Choice(Format(darker), White);
            }
        }

        return new Choice("#000000", White);
    }

    public static Choice ForWhite(string hex)
    {
        var fill = Parse(hex);
        for (var black = 0; black <= 100; black += 5)
        {
            var darker = Mix(fill, (0, 0, 0), black / 100.0);
            if (Ratio(darker, Parse(White)) >= TextMinimum)
            {
                return new Choice(Format(darker), White);
            }
        }

        return new Choice("#000000", White);
    }

    public static string AsText(string hex, string background)
    {
        var color = Parse(hex);
        var ground = Parse(background);
        for (var black = 0; black <= 100; black += 5)
        {
            var darker = Mix(color, (0, 0, 0), black / 100.0);
            if (Ratio(darker, ground) >= TextMinimum)
            {
                return Format(darker);
            }
        }

        return "#000000";
    }

    public static string Mix(string hex, string with, double share) => Format(Mix(Parse(hex), Parse(with), share));

    public static double Ratio(string first, string second) => Ratio(Parse(first), Parse(second));

    public static bool IsHex(string value) =>
        value.Length is 4 or 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

    private static double Ratio((double R, double G, double B) first, (double R, double G, double B) second)
    {
        var one = Luminance(first);
        var other = Luminance(second);
        return (Math.Max(one, other) + 0.05) / (Math.Min(one, other) + 0.05);
    }

    private static double Luminance((double R, double G, double B) color)
    {
        static double Linear(double channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static (double R, double G, double B) Mix((double R, double G, double B) color, (double R, double G, double B) with, double share) =>
        (color.R + (with.R - color.R) * share, color.G + (with.G - color.G) * share, color.B + (with.B - color.B) * share);

    private static (double R, double G, double B) Parse(string hex)
    {
        if (!IsHex(hex))
        {
            throw new FormatException($"'{hex}' is not a hex colour (#rgb or #rrggbb).");
        }

        var digits = hex.Length == 4 ? string.Concat(hex[1], hex[1], hex[2], hex[2], hex[3], hex[3]) : hex[1..];
        return (Channel(digits, 0), Channel(digits, 2), Channel(digits, 4));
    }

    private static double Channel(string digits, int start) => int.Parse(digits.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Format((double R, double G, double B) color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{(int)Math.Round(color.R):x2}{(int)Math.Round(color.G):x2}{(int)Math.Round(color.B):x2}");
}
