namespace Staffora.Blazor.Components.Common.Dialogs;

public sealed class DialogScope
{
    private DialogScope()
    {
    }

    public static DialogScope Instance { get; } = new();
}
