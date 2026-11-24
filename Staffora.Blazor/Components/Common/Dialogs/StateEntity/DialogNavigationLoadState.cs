using System.Collections;
using Staffora.Core.Workflow.Conditions;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

internal sealed class DialogNavigationLoadState : INavigationLoadState
{
    private readonly Dictionary<object, string>? loadedAt;

    private readonly IReadOnlySet<string>? graphPaths;

    public DialogNavigationLoadState()
    {
    }

    public DialogNavigationLoadState(object root, IReadOnlySet<string> graphPaths)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(graphPaths);
        this.graphPaths = graphPaths;
        this.loadedAt = new Dictionary<object, string>(ReferenceEqualityComparer.Instance) { [root] = string.Empty };

        var level = new List<(object Row, string Path)> { (root, string.Empty) };
        while (level.Count > 0)
        {
            var next = new List<(object Row, string Path)>();
            foreach (var (row, path) in level)
            {
                foreach (var navigation in graphPaths.Where(candidate => IsChildOf(candidate, path)))
                {
                    var name = navigation[(path.Length == 0 ? 0 : path.Length + 1)..];
                    foreach (var child in ValuesOf(row.GetType().GetProperty(name)?.GetValue(row)))
                    {
                        if (this.loadedAt.TryAdd(child, navigation))
                        {
                            next.Add((child, navigation));
                        }
                    }
                }
            }

            level = next;
        }
    }

    public bool MissedAny { get; private set; }

    public bool IsLoaded(object entity, string navigation)
    {
        if (this.loadedAt is not null)
        {
            if (!this.loadedAt.TryGetValue(entity, out var at) || !this.graphPaths!.Contains(at.Length == 0 ? navigation : $"{at}.{navigation}"))
            {
                return Missed();
            }
        }

        var type = entity.GetType();
        var property = type.GetProperty(navigation);
        if (property is null)
        {
            return Missed();
        }

        var value = property.GetValue(entity);
        if (value is IEnumerable and not string)
        {
            return true;
        }

        if (type.GetProperty($"{navigation}Id") is not { } foreignKey)
        {
            return true;
        }

        var key = foreignKey.GetValue(entity);
        var names = key is not null && !(key is Guid id && id == Guid.Empty);
        if (value is null)
        {
            return names ? Missed() : true;
        }

        return names && Equals(value.GetType().GetProperty("Id")?.GetValue(value), key) ? true : Missed();
    }

    private static bool IsChildOf(string candidate, string path) =>
        path.Length == 0
            ? !candidate.Contains('.', StringComparison.Ordinal)
            : candidate.StartsWith(path + ".", StringComparison.Ordinal) && !candidate[(path.Length + 1)..].Contains('.', StringComparison.Ordinal);

    private static IEnumerable<object> ValuesOf(object? value) => value switch
    {
        null => [],
        string => [],
        IEnumerable items => items.Cast<object>().Where(item => item is not null),
        _ => [value],
    };

    private bool Missed()
    {
        MissedAny = true;
        return false;
    }
}
