namespace Staffora.Core.Lists;

public static class ClosedDayList
{
    public const string When = "when";

    public const string Upcoming = "upcoming";

    public const string Past = "past";

    public const string SortFirstDay = "from";

    public const string SortName = "name";

    public static ListDefinition Definition { get; } = new(
        sorts: [new(SortFirstDay, "First day"), new(SortName, "Name")],
        defaultSort: SortFirstDay,
        filters: [new ListFilterDefinition(When, "When", [new ListOption(Upcoming, "Today and later"), new ListOption(Past, "Past")])]);
}
