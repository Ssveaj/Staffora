using System.Collections.Frozen;

namespace Staffora.Core.Lists;

public sealed record ListQuery
{
    private static readonly IReadOnlyDictionary<string, string> noFilters = FrozenDictionary<string, string>.Empty;
    private static readonly IReadOnlySet<string> noToggles = FrozenSet<string>.Empty;

    public string Search { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Filters
    {
        get => this.filters;
        init => this.filters = value ?? noFilters;
    }

    public IReadOnlySet<string> Toggles
    {
        get => this.toggles;
        init => this.toggles = value ?? noToggles;
    }

    private readonly IReadOnlyDictionary<string, string> filters = noFilters;
    private readonly IReadOnlySet<string> toggles = noToggles;

    public string? Group { get; init; }

    public string Sort { get; init; } = string.Empty;

    public bool Descending { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = ListDefinition.DefaultPageSize;

    public int Skip => (Page - 1) * PageSize;

    public string? Filter(string key) => Filters.TryGetValue(key, out var value) ? value : null;

    public TEnum? FilterAs<TEnum>(string key) where TEnum : struct, Enum =>
        Filter(key) is { } value && ListOption.TryParseEnum(value, out TEnum member) ? member : null;

    public bool IsOn(string key) => Toggles.Contains(key);

    public ListQuery WithFilter(string key, string? value)
    {
        var filters = new Dictionary<string, string>(Filters, StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value))
        {
            filters.Remove(key);
        }
        else
        {
            filters[key] = value;
        }

        return this with { Filters = filters };
    }

    public ListQuery WithToggle(string key, bool on)
    {
        var toggles = new HashSet<string>(Toggles, StringComparer.Ordinal);
        if (on)
        {
            toggles.Add(key);
        }
        else
        {
            toggles.Remove(key);
        }

        return this with { Toggles = toggles };
    }

    public bool Equals(ListQuery? other) =>
        other is not null
        && Search == other.Search
        && Group == other.Group
        && Sort == other.Sort
        && Descending == other.Descending
        && Page == other.Page
        && PageSize == other.PageSize
        && Filters.Count == other.Filters.Count
        && Filters.All(filter => other.Filters.TryGetValue(filter.Key, out var value) && value == filter.Value)
        && Toggles.SetEquals(other.Toggles);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Search);
        hash.Add(Group);
        hash.Add(Sort);
        hash.Add(Descending);
        hash.Add(Page);
        hash.Add(PageSize);

        hash.Add(Filters.Aggregate(0, (sum, filter) => sum ^ HashCode.Combine(filter.Key, filter.Value)));
        hash.Add(Toggles.Aggregate(0, (sum, toggle) => sum ^ toggle.GetHashCode(StringComparison.Ordinal)));
        return hash.ToHashCode();
    }
}

public readonly record struct ListSort(string Key, bool Descending);
