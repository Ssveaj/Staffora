namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

internal sealed record CustomFieldRequirement(bool IsRequired, IReadOnlyList<string> NeededBefore)
{
    public static CustomFieldRequirement None { get; } = new(false, []);

    public static CustomFieldRequirement Required { get; } = new(true, []);
}
