using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Staffing;

namespace Staffora.Services.Coordination;

public sealed record CoordinationSlotRow(
    Guid SlotId,
    string Slot,
    Guid JobId,
    string Job,
    string Customer,
    JobState JobState,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    PriorityKind Kind,
    IReadOnlyList<string> Reasons,
    DateTime? NextOccurrenceUtc,
    string StartsLabel,
    string StaffMembersLabel)
{
    public string Label => $"{Slot}, {Job}";

    public string PeriodLabel => Times is { } times ? JobRules.WhenText(times) : JobRules.PeriodText(PeriodStart, PeriodEnd);

    public JobTimes? Times { get; init; }

    public int SlotCount { get; init; } = 1;

    public int Assigned { get; init; }

    public int Requested { get; init; }

    public int Accepted { get; init; }

    public int AtRisk { get; init; }

    public int OpenAgain { get; init; }

    public string KindLabel => KindText(Kind);

    public static string KindText(PriorityKind kind) =>
        typeof(PriorityKind).GetField(kind.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name ?? kind.ToString();
}

public sealed record JobBehind(Guid JobId, string Job, JobState State, string Reason);

internal sealed record SlotEntry(
    CoordinationSlotRow Row,
    IReadOnlyList<DateOnly> OccurrenceDays,
    IReadOnlyList<DateOnly> BlockedDays,
    IReadOnlyList<OccurrenceInstant> Occurrences,
    IReadOnlySet<Guid> LiveStaffMembers,
    Guid UnitId,
    Guid CustomerId,
    IReadOnlyList<Guid> Required,
    string? Place = null);

public sealed class CoordinationSnapshot
{
    private readonly IReadOnlyList<SlotEntry> slots;
    private readonly IReadOnlyList<JobBehind> behind;

    internal CoordinationSnapshot(Guid tenantId, IReadOnlyList<SlotEntry> slots, IReadOnlyList<JobBehind> behind, DateOnly today, DateTime readAt, string timeZoneId)
    {
        TenantId = tenantId;
        this.slots = slots;
        this.behind = behind;
        Today = today;
        ReadAt = readAt;
        TimeZoneId = timeZoneId;
    }

    internal Guid TenantId { get; }

    internal IReadOnlyList<SlotEntry> Entries => this.slots;

    internal IReadOnlyList<JobBehind> Behind => this.behind;

    public DateOnly Today { get; }

    public DateTime ReadAt { get; }

    public string TimeZoneId { get; }

    public IReadOnlyList<CoordinationSlotRow> All => [.. this.slots.Select(slot => slot.Row)];

    public static IReadOnlyList<PriorityKind> ShortKinds { get; } = [PriorityKind.AtRisk, PriorityKind.Uncovered, PriorityKind.WaitingForPick, PriorityKind.Unanswered];

    public IReadOnlyList<CoordinationSlotRow> ShortOfStaff() =>
    [
        .. this.slots
            .Select(slot => slot.Row)
            .Where(row => ShortKinds.Contains(row.Kind) && row.NextOccurrenceUtc is not null)
            .OrderBy(row => row.NextOccurrenceUtc)
            .ThenBy(row => PriorityRules.Rank(row.Kind))
            .ThenBy(row => row.Job, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.SlotId),
    ];

    public IReadOnlyDictionary<DateOnly, DayMark> DayMarks(DateOnly from, DateOnly to) =>
        PriorityRules.MarkDays(this.slots.Select(slot => new WeekFacts(slot.Row.Kind, slot.OccurrenceDays, slot.BlockedDays)), from, to);
}
