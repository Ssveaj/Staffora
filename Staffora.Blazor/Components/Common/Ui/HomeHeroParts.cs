using Staffora.Core.Models;
using Staffora.Core.Staffing;

namespace Staffora.Blazor.Components.Common.Ui;

public sealed record HomeRingSegment(string Key, double Value, string Tone);

public sealed record HomeLegendItem(string Key, string Name, string? Note, string? Value, string Tone);

public static class HomeToneClass
{
    public static string Of(HomeTone tone, bool soft = false) => tone switch
    {
        HomeTone.Attention => "attention",
        HomeTone.Problem => "problem",
        HomeTone.Held => "held",
        _ => soft ? "calm-soft" : "calm",
    };

    public static string Text(HomeTone tone) => tone switch
    {
        HomeTone.Attention => " (needs a look)",
        HomeTone.Problem => " (needs you)",
        _ => string.Empty,
    };
}

public static class HomeStaffingParts
{
    public static IReadOnlyList<HomeRingSegment> SlotSegments(HomeStaffing week)
    {
        ArgumentNullException.ThrowIfNull(week);
        return
        [
            new("held", week.WeekSlotsHeld, HomeToneClass.Of(HomeTone.Calm)),
            new("open", week.WeekSlotsOpen, HomeToneClass.Of(HomeTone.Attention)),
        ];
    }

    public static IReadOnlyList<HomeLegendItem> SlotLegend(HomeStaffing week)
    {
        ArgumentNullException.ThrowIfNull(week);
        return
        [
            new("held", "Held", "slots with their staff member", Number(week.WeekSlotsHeld), HomeToneClass.Of(HomeTone.Calm)),
            new("open", "Open", week.WeekSlotsOpen == 0 ? "nobody missing this week" : "slots nobody holds yet", Number(week.WeekSlotsOpen), HomeToneClass.Of(week.WeekSlotsOpen > 0 ? HomeTone.Attention : HomeTone.Held)),
            new("jobs", "Jobs", "with a time this week", Number(week.JobsThisWeek), HomeToneClass.Of(HomeTone.Calm, soft: true)),
        ];
    }

    public static UiTone ShortEdge(PriorityKind kind) => kind switch
    {
        PriorityKind.AtRisk => UiTone.Danger,
        PriorityKind.Uncovered => UiTone.Warning,
        PriorityKind.WaitingForPick => UiTone.Info,
        _ => UiTone.Neutral,
    };

    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
