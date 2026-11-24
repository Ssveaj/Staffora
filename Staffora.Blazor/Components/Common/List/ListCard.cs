using Staffora.Blazor.Components.Common.Ui;

namespace Staffora.Blazor.Components.Common.List;

public sealed record ListCard(string Title)
{
    public DateOnly? Date { get; init; }

    public string? Badge { get; init; }

    public string? BadgeIcon { get; init; }

    public UiTone BadgeTone { get; init; } = UiTone.Warning;

    public string? BadgeKey { get; init; }

    public IReadOnlyList<ListCardFact> Facts { get; init; } = [];

    public string? Hint { get; init; }

    public string? EdgeColor { get; init; }
}

public readonly record struct ListCardFact(string Icon, string Text);
