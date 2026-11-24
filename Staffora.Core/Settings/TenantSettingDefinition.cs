namespace Staffora.Core.Settings;

public enum TenantSettingCategory
{
    General = 1,
}

public enum TenantSettingKind
{
    TimeZone = 1,

    Hours = 2,

    HolidayCalendar = 3,

    Switch = 4,
}

public sealed record TenantSettingDefaults(string PlatformTimeZone);

public abstract class TenantSettingDefinition
{
    public const int KeyMaxLength = 100;

    public const int ValueMaxLength = 400;

    protected TenantSettingDefinition(string key, TenantSettingCategory category, TenantSettingKind kind, string displayName, string description)
    {
        Key = key.Length is > 0 and <= KeyMaxLength ? key : throw new ArgumentException("A setting key is 1 to 100 characters.", nameof(key));
        Category = category;
        Kind = kind;
        DisplayName = displayName;
        Description = description;
    }

    public string Key { get; }

    public TenantSettingCategory Category { get; }

    public TenantSettingKind Kind { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool AllowsEmpty { get; init; }

    public bool IsPinned { get; init; }

    public abstract string DefaultText(TenantSettingDefaults defaults);

    public abstract string? DescribeInvalid(string? text);
}

public sealed class TenantSettingDefinition<T> : TenantSettingDefinition
    where T : notnull
{
    private readonly Func<TenantSettingDefaults, T> defaultValue;
    private readonly TenantSettingDefinition<T>.Parser tryParse;
    private readonly Func<T, string> format;
    private readonly Func<string, string> invalidReason;

    public TenantSettingDefinition(string key, TenantSettingCategory category, TenantSettingKind kind, string displayName, string description, Func<TenantSettingDefaults, T> defaultValue, TenantSettingDefinition<T>.Parser tryParse, Func<T, string> format, Func<string, string> invalidReason) : base(key, category, kind, displayName, description)
    {
        this.defaultValue = defaultValue;
        this.tryParse = tryParse;
        this.format = format;
        this.invalidReason = invalidReason;
    }

    public delegate bool Parser(string text, out T value);

    public T DefaultValue(TenantSettingDefaults defaults) => this.defaultValue(defaults);

    public override string DefaultText(TenantSettingDefaults defaults) => this.format(this.defaultValue(defaults));

    public bool TryParse(string? text, out T value)
    {
        var given = AllowsEmpty ? text ?? string.Empty : text;
        if (given is { Length: <= ValueMaxLength } && (given.Length > 0 || AllowsEmpty) && this.tryParse(given, out value))
        {
            return true;
        }

        value = default!;
        return false;
    }

    public string Format(T value) => this.format(value);

    public override string? DescribeInvalid(string? text) =>
        AllowsEmpty && string.IsNullOrEmpty(text) ? null
        : string.IsNullOrWhiteSpace(text) ? $"{DisplayName} needs a value."
        : text.Length > ValueMaxLength ? $"{DisplayName} is at most {ValueMaxLength} characters."
        : TryParse(text, out _) ? null
        : this.invalidReason(text);
}
