namespace Staffora.Core.Lists;

public static class ErrorLogList
{
    public const string Level = "level";

    public const string Warning = "warning";
    public const string Error = "error";
    public const string Critical = "critical";

    public const string Period = "days";

    public const int DefaultDays = 7;

    public const string ByDay = "day";

    public const string SortTime = "time";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortTime, "Time")],
        defaultSort: SortTime,
        filters:
        [
            new ListFilterDefinition(Level, "Level", [new(Warning, "Warning"), new(Error, "Error"), new(Critical, "Critical")]),
            new ListFilterDefinition(Period, "Period", [new("1", "Last day"), new("30", "Last 30 days"), new("90", "Last 90 days")], AllLabel: "Last 7 days"),
        ],
        groups: [new(ByDay, "Day")],
        defaultGroup: ByDay,
        defaultDescending: true,
        pageSize: 50);

    public static int DaysOf(string? period) => int.TryParse(period, out var days) && days > 0 ? days : DefaultDays;
}
