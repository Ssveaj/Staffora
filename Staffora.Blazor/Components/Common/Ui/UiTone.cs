namespace Staffora.Blazor.Components.Common.Ui;

public enum UiTone
{
    Accent = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Danger = 4,
    Neutral = 5,
}

public static class UiToneClass
{
    public static string Of(UiTone tone) => tone switch
    {
        UiTone.Info => "ui-tone--info",
        UiTone.Success => "ui-tone--success",
        UiTone.Warning => "ui-tone--warning",
        UiTone.Danger => "ui-tone--danger",
        UiTone.Neutral => "ui-tone--neutral",
        _ => "ui-tone--accent",
    };
}
