using Staffora.Blazor.Components.Common.Ui;
using Staffora.Blazor.Helper;
using Staffora.Core;
using Staffora.Core.Annotations;

namespace Staffora.Blazor.Components.Common.List;

public readonly record struct ListRowState(string Label, UiTone Tone)
{
    public static ListRowState Active { get; } = new("Active", UiTone.Success);

    public static ListRowState Inactive { get; } = new("Inactive", UiTone.Neutral);

    public static ListRowState Of(bool isActive) => isActive ? Active : Inactive;

    public static ListRowState Of(Enum state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var tone = state.GetStateCategory() switch
        {
            StateCategory.Entry => UiTone.Info,
            StateCategory.Open or StateCategory.Done => UiTone.Success,
            _ => UiTone.Neutral,
        };
        return new ListRowState(EnumDisplayHelper.GetDisplayName(state), tone);
    }
}
