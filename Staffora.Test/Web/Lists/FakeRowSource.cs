using Staffora.Core.Lists;
using Staffora.Core.Models;

namespace Staffora.Test.Web.Lists;

public sealed record ListTestRow(Guid Id, string Name, string Team, bool IsActive);

public sealed class FakeRowSource
{
    public const string Team = "team";
    public const string ShowInactive = "inactive";

    public FakeRowSource(IEnumerable<ListTestRow> rows)
    {
        Rows = [.. rows];
    }

    public static ListDefinition Definition { get; } = new(
        sorts: [new("name", "Name"), new("team", "Team")],
        defaultSort: "name",
        filters: [new ListFilterDefinition(Team, "Team", [new("alpha", "Alpha"), new("beta", "Beta")])],
        toggles: [new ListToggleDefinition(ShowInactive, "Show inactive rows", "1 inactive row is hidden.", "{0} inactive rows are hidden.")],
        groups: [new("team", "Team")],
        pageSize: 2);

    public List<ListTestRow> Rows { get; }

    public List<ListQuery> Queries { get; } = [];

    public Func<ListQuery, Task>? Hold { get; set; }

    public string? Failure { get; set; }

    public async Task<Result<ListPage<ListTestRow>>> LoadAsync(ListQuery query)
    {
        Queries.Add(query);
        if (Hold is not null)
        {
            await Hold(query);
        }

        if (Failure is not null)
        {
            return Result<ListPage<ListTestRow>>.Failure(Failure);
        }

        var matching = Rows.Where(row => query.Search.Length == 0 || row.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || row.Team.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        if (query.Filter(Team) is { } team)
        {
            matching = matching.Where(row => row.Team.Equals(team, StringComparison.OrdinalIgnoreCase));
        }

        var hidden = new Dictionary<string, int>();
        if (!query.IsOn(ShowInactive))
        {
            hidden[ShowInactive] = matching.Count(row => !row.IsActive);
            matching = matching.Where(row => row.IsActive);
        }

        IEnumerable<ListTestRow> ordered = query.Group == "team" ? matching.OrderBy(row => row.Team) : matching.OrderBy(_ => 0);
        Func<ListTestRow, string> key = query.Sort == "team" ? row => row.Team : row => row.Name;
        ordered = query.Descending ? ((IOrderedEnumerable<ListTestRow>)ordered).ThenByDescending(key) : ((IOrderedEnumerable<ListTestRow>)ordered).ThenBy(key);
        var all = ordered.ToList();
        var page = ListPage<ListTestRow>.PageWithin(query, all.Count);
        var rows = all.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToList();
        var result = query.Group == "team"
            ? ListPage<ListTestRow>.Grouped(rows, all.Count, page, query.PageSize, row => row.Team.ToLowerInvariant(),
                all.GroupBy(row => row.Team.ToLowerInvariant()).ToDictionary(group => group.Key, group => ($"Team {group.First().Team}", group.Count())))
            : ListPage<ListTestRow>.Ungrouped(rows, all.Count, page, query.PageSize);
        return Result<ListPage<ListTestRow>>.Success(result with { HiddenCounts = hidden });
    }
}
