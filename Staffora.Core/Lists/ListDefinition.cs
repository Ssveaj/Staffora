using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;

namespace Staffora.Core.Lists;

public sealed record ListOption(string Value, string Label)
{
    public static IReadOnlyList<ListOption> ForEnum<TEnum>() where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(member => new ListOption(member.ToString().ToLowerInvariant(), typeof(TEnum).GetField(member.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? member.ToString()))];

    public static bool TryParseEnum<TEnum>(string value, out TEnum member) where TEnum : struct, Enum
    {
        member = default;
        return value.Length > 0
            && char.IsAsciiLetter(value[0])
            && value.All(char.IsAsciiLetterOrDigit)
            && Enum.TryParse(value, ignoreCase: true, out member)
            && Enum.IsDefined(member);
    }
}

public sealed record ListFilterDefinition(string Key, string Label, IReadOnlyList<ListOption> Options, string AllLabel = "All");

public sealed record ListToggleDefinition(string Key, string Label, string HiddenOne, string HiddenMany);

public sealed class ListDefinition
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxPage = 10_000;

    public const int MaxSearchLength = 100;

    public const string SearchKey = "q";

    public const string GroupKey = "group";

    public const string SortKey = "sort";

    public const string PageKey = "page";

    public const string NoGroup = "none";

    public const string DialogStackKey = "dlg";

    private static readonly HashSet<string> reserved = new(StringComparer.Ordinal) { SearchKey, GroupKey, SortKey, PageKey, DialogStackKey };

    public ListDefinition(
        IReadOnlyList<ListOption> sorts,
        string defaultSort,
        IReadOnlyList<ListFilterDefinition>? filters = null,
        IReadOnlyList<ListToggleDefinition>? toggles = null,
        IReadOnlyList<ListOption>? groups = null,
        string? defaultGroup = null,
        bool defaultDescending = false,
        int pageSize = DefaultPageSize)
    {
        Sorts = sorts ?? throw new ArgumentNullException(nameof(sorts));
        Filters = filters ?? [];
        Toggles = toggles ?? [];
        Groups = groups ?? [];
        DefaultSort = defaultSort;
        DefaultDescending = defaultDescending;
        DefaultGroup = defaultGroup;
        PageSize = pageSize;
        Validate();
    }

    public IReadOnlyList<ListOption> Sorts { get; }

    public IReadOnlyList<ListFilterDefinition> Filters { get; }

    public IReadOnlyList<ListToggleDefinition> Toggles { get; }

    public IReadOnlyList<ListOption> Groups { get; }

    public string DefaultSort { get; }

    public bool DefaultDescending { get; }

    public string? DefaultGroup { get; }

    public int PageSize { get; }

    public IReadOnlyList<string> AddressKeys => [SearchKey, GroupKey, SortKey, PageKey, .. Filters.Select(filter => filter.Key), .. Toggles.Select(toggle => toggle.Key)];

    public static string AddressName(string? prefix, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (prefix is null)
        {
            return key;
        }

        if (!IsKey(prefix) || prefix == DialogStackKey)
        {
            throw new ArgumentException($"\"{prefix}\" cannot be a list's address prefix: use 1 to 16 lowercase letters or digits, starting with a letter, and not \"{DialogStackKey}\".", nameof(prefix));
        }

        return $"{prefix}.{key}";
    }

    public ListQuery Default => new() { Sort = DefaultSort, Descending = DefaultDescending, Group = DefaultGroup, PageSize = PageSize };

    public ListQuery Normalize(ListQuery? query)
    {
        if (query is null)
        {
            return Default;
        }

        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var filter in Filters)
        {
            if (ParseFilter(filter, query.Filter(filter.Key)) is { } value)
            {
                filters[filter.Key] = value;
            }
        }

        var toggles = Toggles.Where(toggle => query.IsOn(toggle.Key)).Select(toggle => toggle.Key).ToHashSet(StringComparer.Ordinal);
        var sort = Sorts.Any(option => option.Value == query.Sort) ? new ListSort(query.Sort, query.Descending) : new ListSort(DefaultSort, DefaultDescending);
        var group = query.Group is null || Groups.Any(option => option.Value == query.Group) ? query.Group : DefaultGroup;

        return new ListQuery
        {
            Search = CutSearch(query.Search),
            Filters = filters,
            Toggles = toggles,
            Group = group,
            Sort = sort.Key,
            Descending = sort.Descending,
            Page = query.Page is >= 1 and <= MaxPage ? query.Page : 1,
            PageSize = PageSize,
        };
    }

    public ListQuery Read(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var query = Default with
        {
            Search = ParseSearch(read(SearchKey)),
            Group = ParseGroup(read(GroupKey)),
            Page = ParsePage(read(PageKey)),
        };

        if (ParseSort(read(SortKey)) is { } sort)
        {
            query = query with { Sort = sort.Key, Descending = sort.Descending };
        }

        foreach (var filter in Filters)
        {
            query = query.WithFilter(filter.Key, ParseFilter(filter, read(filter.Key)));
        }

        foreach (var toggle in Toggles)
        {
            query = query.WithToggle(toggle.Key, ParseToggle(read(toggle.Key)));
        }

        return query;
    }

    public IReadOnlyDictionary<string, string?> Write(ListQuery query)
    {
        var normalized = Normalize(query);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SearchKey] = normalized.Search.Length == 0 ? null : normalized.Search,
            [GroupKey] = FormatGroup(normalized.Group),
            [SortKey] = FormatSort(new ListSort(normalized.Sort, normalized.Descending)),
            [PageKey] = normalized.Page == 1 ? null : normalized.Page.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var filter in Filters)
        {
            values[filter.Key] = normalized.Filter(filter.Key);
        }

        foreach (var toggle in Toggles)
        {
            values[toggle.Key] = normalized.IsOn(toggle.Key) ? "true" : null;
        }

        return values;
    }

    public static string ParseSearch(string? text) =>
        text?.Trim() is { Length: > 0 and <= MaxSearchLength } trimmed ? trimmed : string.Empty;

    public static string? ParseFilter(ListFilterDefinition filter, string? text) =>
        string.IsNullOrEmpty(text) ? null : filter.Options.FirstOrDefault(option => string.Equals(option.Value, text, StringComparison.OrdinalIgnoreCase))?.Value;

    public static bool ParseToggle(string? text) => bool.TryParse(text, out var on) && on;

    public string? ParseGroup(string? text)
    {
        if (string.Equals(text, NoGroup, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Groups.FirstOrDefault(option => string.Equals(option.Value, text, StringComparison.OrdinalIgnoreCase))?.Value ?? DefaultGroup;
    }

    public string? FormatGroup(string? group) => group == DefaultGroup ? null : group ?? NoGroup;

    public ListSort? ParseSort(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var descending = text[0] == '-';
        var key = descending ? text[1..] : text;
        return Sorts.FirstOrDefault(option => string.Equals(option.Value, key, StringComparison.OrdinalIgnoreCase)) is { } match
            ? new ListSort(match.Value, descending)
            : null;
    }

    public string? FormatSort(ListSort sort) =>
        sort.Key == DefaultSort && sort.Descending == DefaultDescending ? null : (sort.Descending ? "-" : string.Empty) + sort.Key;

    public static int ParsePage(string? text) =>
        text is { Length: > 0 and <= 5 } && text.All(char.IsAsciiDigit) && int.Parse(text, CultureInfo.InvariantCulture) is var page and >= 1 and <= MaxPage ? page : 1;

    private static string CutSearch(string? search) =>
        (search ?? string.Empty).Trim() is var trimmed && trimmed.Length > MaxSearchLength ? trimmed[..MaxSearchLength].TrimEnd() : trimmed;

    private void Validate()
    {
        if (PageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize), PageSize, $"A page holds 1 to {MaxPageSize} rows.");
        }

        if (Sorts.Count == 0 || Sorts.All(option => option.Value != DefaultSort))
        {
            throw new ArgumentException($"The default sort \"{DefaultSort}\" is not one of the sorts on offer.");
        }

        if (DefaultGroup is not null && Groups.All(option => option.Value != DefaultGroup))
        {
            throw new ArgumentException($"The default grouping \"{DefaultGroup}\" is not one of the groupings on offer.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Filters.Select(filter => filter.Key).Concat(Toggles.Select(toggle => toggle.Key)))
        {
            if (!IsKey(key) || reserved.Contains(key) || !keys.Add(key))
            {
                throw new ArgumentException($"\"{key}\" cannot be a list's address key: use 1 to 16 lowercase letters or digits, not a key of the list's own or one used twice.");
            }
        }

        var values = Sorts.Concat(Groups).Concat(Filters.SelectMany(filter => filter.Options)).Select(option => option.Value);
        if (values.FirstOrDefault(value => !IsToken(value)) is { } bad)
        {
            throw new ArgumentException($"\"{bad}\" cannot be a list option's value: use 1 to 64 lowercase letters, digits or hyphens, starting with a letter or digit.");
        }

        if (Groups.Any(option => option.Value == NoGroup) || Sorts.Select(option => option.Value).Distinct().Count() != Sorts.Count || Groups.Select(option => option.Value).Distinct().Count() != Groups.Count)
        {
            throw new ArgumentException($"A list's sorts and groupings each have unique values, and no grouping is called \"{NoGroup}\".");
        }
    }

    private static bool IsKey(string key) =>
        key is { Length: > 0 and <= 16 } && key[0] is >= 'a' and <= 'z' && key.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    private static bool IsToken(string value) =>
        value is { Length: > 0 and <= 64 } && value[0] is (>= 'a' and <= 'z') or (>= '0' and <= '9')
        && value.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
