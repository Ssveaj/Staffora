using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Scheduling;

namespace Staffora.Core.Models;

public sealed record StaffListItem(Guid ResourceId, Guid UserId, string Name, string Email, ResourceState State)
{
    public IReadOnlyList<string> Units { get; init; } = [];

    public StaffPatternCover? WorkPattern { get; init; }

    public string WorkPatternLabel => WorkPattern?.Label ?? StaffPatternCover.NoneLabel;
}

public sealed record StaffPatternCover(string Pattern, DateOnly From, DateOnly? To, DateOnly Today)
{
    public const int EndingSoonDays = 30;

    public const string NoneLabel = "No work pattern";

    public bool Current => From <= Today && (To is not { } to || to >= Today);

    public bool EndsSoon => Current && To is { } to && to <= Today.AddDays(EndingSoonDays);

    public string Label => Current
        ? To is { } to ? $"{Pattern} · ends {ScheduleRules.DateText(to)}" : $"{Pattern} · since {ScheduleRules.DateText(From)}"
        : $"From {ScheduleRules.DateText(From)}: {Pattern}";

    public static StaffPatternCover? Of(IEnumerable<(string Pattern, DateOnly From, DateOnly? To)> active, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(active);
        var covers = active
            .Where(assignment => assignment.To is not { } to || to >= today)
            .Select(assignment => new StaffPatternCover(assignment.Pattern, assignment.From, assignment.To, today))
            .ToList();
        return covers.FirstOrDefault(cover => cover.Current) ?? covers.OrderBy(cover => cover.From).FirstOrDefault();
    }
}

public sealed record StaffSummary(Guid ResourceId, ResourceState State);

public sealed record StaffUnitRow(Guid UnitId, string Unit, OrganizationUnitState UnitState, bool UnitIsRoot)
{
    public string UnitLabel => Unit + OrganizationUnitOutline.MarkOf(UnitState, UnitIsRoot);
}
