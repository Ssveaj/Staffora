namespace Staffora.Core.Lists;

public sealed record ListPage<TRow>(IReadOnlyList<ListSection<TRow>> Sections, int TotalCount, int Page, int PageSize)
{
    public IReadOnlyDictionary<string, int> HiddenCounts { get; init; } = new Dictionary<string, int>();

    public IEnumerable<TRow> Rows => Sections.SelectMany(section => section.Rows);

    public int PageCount => TotalCount <= 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;

    public static int PageWithin(ListQuery query, int totalCount) =>
        totalCount <= 0 ? 1 : Math.Min(query.Page, (totalCount + query.PageSize - 1) / query.PageSize);

    public static ListPage<TRow> Ungrouped(IReadOnlyList<TRow> rows, int totalCount, int page, int pageSize) =>
        new([new ListSection<TRow>(null, null, totalCount, rows)], totalCount, page, pageSize);

    public static ListPage<TRow> Grouped(IReadOnlyList<TRow> rows, int totalCount, int page, int pageSize, Func<TRow, string> groupOf, IReadOnlyDictionary<string, (string Label, int Count)> groups)
    {
        ArgumentNullException.ThrowIfNull(groupOf);
        ArgumentNullException.ThrowIfNull(groups);
        var sections = new List<ListSection<TRow>>();
        var start = 0;
        for (var index = 1; index <= rows.Count; index++)
        {
            if (index < rows.Count && groupOf(rows[index]) == groupOf(rows[start]))
            {
                continue;
            }

            var key = groupOf(rows[start]);
            var (label, count) = groups.TryGetValue(key, out var group) ? group : (key, index - start);
            sections.Add(new ListSection<TRow>(key, label, count, rows.Skip(start).Take(index - start).ToList()));
            start = index;
        }

        return new ListPage<TRow>(sections, totalCount, page, pageSize);
    }
}

public sealed record ListSection<TRow>(string? Key, string? Label, int TotalCount, IReadOnlyList<TRow> Rows);
