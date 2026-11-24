namespace Staffora.Blazor.Helper;

public static class DialogStateColors
{
    public sealed record Colors(string State, string HeaderFill, string HeaderText, string PillFill, string PillText, string StateText);

    public static Colors? For(string? stateColor)
    {
        if (stateColor is null || !ColorContrast.IsHex(stateColor))
        {
            return null;
        }

        var header = ColorContrast.ForWhite(stateColor);
        return new Colors(stateColor, header.Fill, header.Foreground, header.Fill, header.Foreground, ColorContrast.AsText(stateColor, ColorContrast.White));
    }

    public static string? Style(string? stateColor) =>
        For(stateColor) is { } colors
            ? $"--dialog-state-color: {colors.State}; --dialog-header-fill: {colors.HeaderFill}; --dialog-header-text: {colors.HeaderText}; --dialog-pill-fill: {colors.PillFill}; --dialog-pill-text: {colors.PillText}; --dialog-state-text: {colors.StateText};"
            : null;
}
