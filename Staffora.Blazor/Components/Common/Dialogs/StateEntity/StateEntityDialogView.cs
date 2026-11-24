using System.Text.RegularExpressions;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Radzen;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

public record StateEntityDialogView(string Key, string Label)
{
    public int Order { get; init; }

    public string? Icon { get; init; }

    public RolePermission? Permission { get; init; }

    public string? Area { get; init; }

    public Func<bool>? When { get; init; }

    public bool StoredOnly { get; init; }

    public virtual bool ReadsStoredRows => false;

    internal static bool IsValidKey(string? key) => key is { Length: > 0 and <= 32 } && keyPattern.IsMatch(key);

    private static readonly Regex keyPattern = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);
}

public sealed record StateEntityDialogListView<TRow>(string Key, string Label, ListDefinition Definition, Func<ListQuery, CancellationToken, Task<Result<ListPage<TRow>>>> Load)
    : StateEntityDialogView(Key, Label)
{
    public override bool ReadsStoredRows => true;

    public string? AddLabel { get; init; }

    public Func<DialogService, CancellationToken, Task<bool>>? Add { get; init; }

    public Func<bool>? CanAdd { get; init; }

    public StateEntityDialogAddChoice? AddChoice { get; init; }
}

public sealed record StateEntityDialogAddChoice(string Label, string Name, string NoneText, Func<IReadOnlyList<ListOption>> Options, Action<string?> Chosen);
