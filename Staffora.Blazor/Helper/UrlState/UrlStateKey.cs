using System.Globalization;

namespace Staffora.Blazor.Helper.UrlState;

public sealed class UrlStateKey<T>
{
    private readonly Func<string, (bool Parsed, T Value)> parse;
    private readonly Func<T, string?> format;

    public UrlStateKey(string name, T defaultValue, Func<string, (bool Parsed, T Value)> parse, Func<T, string?> format)
    {
        if (!UrlStateKeys.IsValidName(name))
        {
            throw new ArgumentException($"\"{name}\" is not a query-string key: use 1 to 16 lowercase ASCII letters or digits, starting with a letter.", nameof(name));
        }

        Name = name;
        Default = defaultValue;
        this.parse = parse ?? throw new ArgumentNullException(nameof(parse));
        this.format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public string Name { get; }

    public T Default { get; }

    public T Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > UrlStateKeys.MaxValueLength)
        {
            return Default;
        }

        try
        {
            var (parsed, value) = this.parse(text);
            return parsed ? value : Default;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Default;
        }
    }

    public string? Format(T value)
    {
        if (EqualityComparer<T>.Default.Equals(value, Default))
        {
            return null;
        }

        var text = this.format(value);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException;
}

public static class UrlStateKey
{
    public static UrlStateKey<string> Text(string name, int maxLength = 100) =>
        new(name, string.Empty,
            text => text.Trim() is { Length: > 0 } trimmed && trimmed.Length <= maxLength ? (true, trimmed) : (false, string.Empty),
            value => (value ?? string.Empty).Trim() is var trimmed && trimmed.Length > maxLength ? trimmed[..maxLength].TrimEnd() : trimmed);

    public static UrlStateKey<TEnum> Enum<TEnum>(string name, TEnum defaultValue, Func<TEnum, bool>? isValid = null) where TEnum : struct, Enum =>
        new(name, defaultValue,
            text => TryParseEnum(text, out TEnum value) && (isValid?.Invoke(value) ?? true) ? (true, value) : (false, defaultValue),
            value => value.ToString().ToLowerInvariant());

    public static UrlStateKey<TEnum?> OptionalEnum<TEnum>(string name, Func<TEnum, bool>? isValid = null) where TEnum : struct, Enum =>
        new(name, null,
            text => TryParseEnum(text, out TEnum value) && (isValid?.Invoke(value) ?? true) ? (true, value) : (false, null),
            value => value?.ToString().ToLowerInvariant());

    public static UrlStateKey<int> Int(string name, int defaultValue, Func<int, bool>? isValid = null) =>
        new(name, defaultValue,
            text => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && (isValid?.Invoke(value) ?? true) ? (true, value) : (false, defaultValue),
            value => value.ToString(CultureInfo.InvariantCulture));

    public static UrlStateKey<Guid?> Guid(string name, Func<Guid, bool>? isValid = null) =>
        new(name, null,
            text => System.Guid.TryParseExact(text, "D", out var value) && value != System.Guid.Empty && (isValid?.Invoke(value) ?? true) ? (true, value) : (false, null),
            value => value?.ToString("D"));

    public static UrlStateKey<DateOnly?> Date(string name, Func<DateOnly, bool>? isValid = null) =>
        new(name, null,
            text => DateOnly.TryParseExact(text, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && (isValid?.Invoke(value) ?? true) ? (true, value) : (false, null),
            value => value?.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture));

    public static UrlStateKey<DateOnly?> Month(string name, Func<DateOnly, bool>? isValid = null) =>
        new(name, null,
            text => DateOnly.TryParseExact($"{text}-01", "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && text.Length == 7 && (isValid?.Invoke(value) ?? true) ? (true, value) : (false, null),
            value => value?.ToString("yyyy'-'MM", CultureInfo.InvariantCulture));

    public static UrlStateKey<bool> Bool(string name, bool defaultValue = false) =>
        new(name, defaultValue,
            text => bool.TryParse(text, out var value) ? (true, value) : (false, defaultValue),
            value => value ? "true" : "false");

    private static bool TryParseEnum<TEnum>(string text, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]) || text.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return false;
        }

        return System.Enum.TryParse(text, ignoreCase: true, out value) && System.Enum.IsDefined(value);
    }
}
