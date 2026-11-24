using Staffora.Core.Entities.Organization;

namespace Staffora.Core.Models;

public static class OrganizationUnitOutline
{
    public const string PathSeparator = " › ";

    public const string InactiveMark = " (inactive)";

    public const string ArchivedMark = " (archived)";

    public static string MarkOf(OrganizationUnitState state, bool isRoot = false) =>
        isRoot || state == OrganizationUnitState.Active ? string.Empty : state == OrganizationUnitState.Archived ? ArchivedMark : InactiveMark;

    public sealed record Entry(OrganizationUnitNode Unit, int Depth, string Path, bool IsLinked)
    {
        public string Label => Path + MarkOf(Unit.State, Unit.IsRoot);
    }

    public static IReadOnlyList<Entry> Of(IEnumerable<OrganizationUnitNode> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var all = units.ToList();
        var ids = all.Select(unit => unit.Id).ToHashSet();
        var children = all
            .Where(unit => unit.ParentId is { } parent && ids.Contains(parent))
            .OrderBy(unit => unit.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToLookup(unit => unit.ParentId!.Value);
        var tops = all
            .Where(unit => unit.ParentId is not { } parent || !ids.Contains(parent))
            .OrderBy(unit => unit.IsRoot ? 0 : 1)
            .ThenBy(unit => unit.Name, StringComparer.CurrentCultureIgnoreCase);

        var entries = new List<Entry>(all.Count);
        var placed = new HashSet<Guid>();

        var pending = new Stack<(OrganizationUnitNode Unit, int Depth, string? ParentPath)>();
        foreach (var top in tops.Reverse())
        {
            pending.Push((top, 0, null));
        }

        while (pending.TryPop(out var next))
        {
            if (!placed.Add(next.Unit.Id))
            {
                continue;
            }

            var path = next.ParentPath is null ? next.Unit.Name : next.ParentPath + PathSeparator + next.Unit.Name;
            entries.Add(new Entry(next.Unit, next.Depth, path, IsLinked: true));
            foreach (var child in children[next.Unit.Id].Reverse())
            {
                pending.Push((child, next.Depth + 1, path));
            }
        }

        foreach (var unit in all.Where(unit => !placed.Contains(unit.Id)).OrderBy(unit => unit.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            placed.Add(unit.Id);
            entries.Add(new Entry(unit, 0, unit.Name, IsLinked: false));
        }

        return entries;
    }
}
