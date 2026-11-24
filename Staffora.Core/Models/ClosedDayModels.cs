using Staffora.Core.Scheduling;

namespace Staffora.Core.Models;

public sealed record ClosedDayListItem(Guid Id, string Name, DateOnly FirstDay, DateOnly LastDay, string? Unit)
{
    public string PeriodText => ClosedDayRules.PeriodText(FirstDay, LastDay);

    public int Days => ClosedDayRules.DayCount(FirstDay, LastDay);

    public string DaysText => Days == 1 ? "1 day" : $"{Days} days";

    public string AppliesToText => Unit ?? ClosedDayDraft.WholeOrganization;
}

public sealed record ClosedDayDraft(string Name, DateOnly FirstDay, DateOnly LastDay, Guid? OrganizationUnitId)
{
    public const string WholeOrganization = "The whole organization";
}

public sealed record ClosedDayUnitChoice(Guid Id, string Name)
{
    public static ClosedDayUnitChoice WholeOrganization { get; } = new(Guid.Empty, ClosedDayDraft.WholeOrganization);

    public static Guid? UnitOf(Guid chosen) => chosen == Guid.Empty ? null : chosen;
}

public sealed record ClosedDayEditor(Guid? Id, ClosedDayDraft Draft, bool CanEdit, IReadOnlyList<ClosedDayUnitChoice> Units);
