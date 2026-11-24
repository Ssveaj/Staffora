using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Staffing;

public enum PriorityKind
{
    [Display(Name = "At risk")]
    AtRisk = 0,

    [Display(Name = "Uncovered")]
    Uncovered = 1,

    [Display(Name = "Waiting for a pick")]
    WaitingForPick = 2,

    [Display(Name = "Unanswered")]
    Unanswered = 3,

    [Display(Name = "Covered")]
    Covered = 4,

    [Display(Name = "Awaits your reply")]
    AwaitsReply = 5,
}

public sealed record HolderFacts(Guid ResourceId, string Name, StaffingVerdict Verdict);

public sealed record SlotFacts(IReadOnlyList<OccurrenceInstant> RemainingOccurrences, bool PeriodStarted, HolderFacts? Holder, int Accepted, int Requested, IReadOnlyList<string>? AwaitingReply = null)
{
    public IReadOnlyList<string> Awaiting => AwaitingReply ?? [];

    public int Count { get; init; } = 1;

    public IReadOnlyList<HolderFacts> Holders { get; init; } = Holder is null ? [] : [Holder];

    public int Expired { get; init; }
}

public sealed record PriorityFacts(Guid SlotId, string Job, string Slot, PriorityKind Kind, DateTime? NextOccurrenceUtc);

public sealed record DayMark(DateOnly Day, int Uncovered, int AtRisk, int Waiting, int Covered)
{
    public int Total => Uncovered + AtRisk + Waiting + Covered;
}

public sealed record WeekFacts(PriorityKind Kind, IReadOnlyCollection<DateOnly> OccurrenceDays, IReadOnlyCollection<DateOnly> BlockedDays);

public static class PriorityRules
{
    public static readonly TimeSpan UrgentWithin = TimeSpan.FromDays(14);

    public const string NobodyAskedReason = "No staff member is asked yet.";

    public static PriorityKind Classify(SlotFacts slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return slot.Holders.Any(holder => holder.Verdict.IsBlocked) ? PriorityKind.AtRisk
            : slot.Holders.Count > 0 && slot.Holders.Count >= slot.Count ? slot.Awaiting.Count > 0 ? PriorityKind.AwaitsReply : PriorityKind.Covered
            : slot.Accepted > 0 ? PriorityKind.WaitingForPick
            : slot.Requested > 0 ? PriorityKind.Unanswered
            : PriorityKind.Uncovered;
    }

    public static IReadOnlyList<string> Reasons(SlotFacts slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        IReadOnlyList<string> reasons = Classify(slot) switch
        {
            PriorityKind.AtRisk when slot.Holders.FirstOrDefault(holder => holder.Verdict.IsBlocked) is { } holder =>
                [$"{holder.Name}, the assigned staff member, is not free for {holder.Verdict.BlockedOccurrences} of {Occurrences(holder.Verdict.Occurrences)}.", .. holder.Verdict.BlockGroups.Select(group => group.Text + ".")],
            PriorityKind.WaitingForPick => [slot.Accepted == 1 ? "1 staff member said yes: pick who works." : $"{slot.Accepted} staff members said yes: pick who works."],
            PriorityKind.Unanswered => [slot.Requested == 1 ? "1 staff member is asked and has not answered." : $"{slot.Requested} staff members are asked and have not answered."],
            PriorityKind.Uncovered => slot.Expired > 0 ? [NobodyAskedReason, ExpiredText(slot.Expired)] : [NobodyAskedReason],
            _ => [],
        };
        return slot.Awaiting.Count == 0 ? reasons : [.. reasons, AwaitingText(slot.Awaiting)];
    }

    public static string ExpiredText(int expired) =>
        expired == 1 ? "1 request expired with no answer." : $"{expired} requests expired with no answer.";

    public static string AwaitingText(IReadOnlyList<string> staffMembers)
    {
        ArgumentNullException.ThrowIfNull(staffMembers);
        return staffMembers.Count switch
        {
            0 => string.Empty,
            1 => $"{staffMembers[0]} wrote and waits for your reply.",
            _ => $"{string.Join(", ", staffMembers.Take(staffMembers.Count - 1))} and {staffMembers[^1]} wrote and wait for your reply.",
        };
    }

    public static int Rank(PriorityKind kind) => kind switch
    {
        PriorityKind.AtRisk => 0,
        PriorityKind.Uncovered => 1,
        PriorityKind.WaitingForPick => 2,
        PriorityKind.AwaitsReply => 3,
        PriorityKind.Unanswered => 4,
        _ => 5,
    };

    public static DateTime? NextOccurrenceUtc(SlotFacts slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return slot.RemainingOccurrences.Count == 0 ? null : slot.RemainingOccurrences.Min(occurrence => occurrence.StartUtc);
    }

    public static string StartsLabel(DateTime? nextOccurrenceUtc, bool periodStarted, DateTime nowUtc)
    {
        if (nextOccurrenceUtc is not { } next)
        {
            return "No occurrences left";
        }

        var left = next - nowUtc;
        if (left <= TimeSpan.Zero)
        {
            return "A time is on now";
        }

        var when = left.TotalHours < 1 ? Count((int)Math.Max(1, Math.Floor(left.TotalMinutes)), "minute")
            : left.TotalHours < 48 ? Count((int)Math.Floor(left.TotalHours), "hour")
            : left.TotalDays < 14 ? Count((int)Math.Floor(left.TotalDays), "day")
            : left.TotalDays < 60 ? Count((int)Math.Floor(left.TotalDays / 7), "week")
            : Count((int)Math.Floor(left.TotalDays / 30), "month");
        return periodStarted ? $"Next occurrence in {when}" : $"Starts in {when}";
    }

    public static bool IsUrgent(DateTime? nextOccurrenceUtc, DateTime nowUtc) => nextOccurrenceUtc is { } next && next - nowUtc <= UrgentWithin;

    public static IReadOnlyList<T> Order<T>(IEnumerable<T> rows, Func<T, PriorityFacts> facts, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(facts);
        return [.. rows
            .Select(row => (Row: row, Facts: facts(row)))
            .Where(pair => pair.Facts.Kind != PriorityKind.Covered)
            .OrderBy(pair => IsUrgent(pair.Facts.NextOccurrenceUtc, nowUtc) ? 0 : 1)
            .ThenBy(pair => Rank(pair.Facts.Kind))
            .ThenBy(pair => pair.Facts.NextOccurrenceUtc ?? DateTime.MaxValue)
            .ThenBy(pair => pair.Facts.Job, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(pair => pair.Facts.Slot, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(pair => pair.Facts.SlotId)
            .Select(pair => pair.Row)];
    }

    public static IReadOnlyList<PriorityFacts> Order(IEnumerable<PriorityFacts> facts, DateTime nowUtc) => Order(facts, fact => fact, nowUtc);

    public static IReadOnlyDictionary<DateOnly, DayMark> MarkDays(IEnumerable<WeekFacts> slots, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var marks = new Dictionary<DateOnly, (int Uncovered, int AtRisk, int Waiting, int Covered)>();
        foreach (var slot in slots)
        {
            foreach (var day in slot.OccurrenceDays.Where(day => day >= from && day <= to).Distinct())
            {
                var (uncovered, atRisk, waiting, covered) = marks.GetValueOrDefault(day);
                switch (slot.Kind)
                {
                    case PriorityKind.Uncovered or PriorityKind.Unanswered:
                        uncovered++;
                        break;
                    case PriorityKind.WaitingForPick:
                        waiting++;
                        break;
                    case PriorityKind.AtRisk when slot.BlockedDays.Contains(day):
                        atRisk++;
                        break;
                    default:
                        covered++;
                        break;
                }

                marks[day] = (uncovered, atRisk, waiting, covered);
            }
        }

        return marks.ToDictionary(entry => entry.Key, entry => new DayMark(entry.Key, entry.Value.Uncovered, entry.Value.AtRisk, entry.Value.Waiting, entry.Value.Covered));
    }

    private static string Occurrences(int count) => count == 1 ? "its 1 remaining occurrence" : $"its {count} remaining occurrences";

    private static string Count(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
