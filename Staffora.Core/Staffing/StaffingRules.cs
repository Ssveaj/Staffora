using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Jobs;
using Staffora.Core.Scheduling;

namespace Staffora.Core.Staffing;

public sealed record OccurrenceInstant(DateOnly Date, TimeOnly Start, TimeOnly End, DateTime StartUtc, DateTime EndUtc)
{
    public DateOnly EndDate => End <= Start ? Date.AddDays(1) : Date;

    public bool CrossesMidnight => EndDate > Date;

    public string Text => $"{ScheduleRules.DateText(Date)} {WorkPatternRules.TimeText(Start)}-{WorkPatternRules.TimeText(End)}{(CrossesMidnight ? " (+1 day)" : string.Empty)}";
}

public sealed record OccurrenceFinding(OccurrenceInstant Occurrence, string Reason, Guid? SourceId);

public sealed record FindingGroup(string Reason, Guid? SourceId, DateOnly First, DateOnly Last, int Occurrences)
{
    public string TextAbout(string? about)
    {
        var reason = string.IsNullOrWhiteSpace(about) ? Reason : $"{Reason} ({about})";
        return Occurrences == 1
            ? $"{reason}: 1 occurrence, {ScheduleRules.DateText(First)}"
            : First == Last
                ? $"{reason}: {Occurrences} occurrences on {ScheduleRules.DateText(First)}"
                : $"{reason}: {Occurrences} occurrences, {ScheduleRules.DateText(First)} to {ScheduleRules.DateText(Last)}";
    }

    public string Text => TextAbout(null);

    public string WhenText => Occurrences == 1
        ? ScheduleRules.DateText(First)
        : First == Last
            ? $"{ScheduleRules.DateText(First)}, {Occurrences} occurrences"
            : $"{ScheduleRules.DateText(First)} to {ScheduleRules.DateText(Last)}, {Occurrences} occurrences";

    public FindingLine LineAbout(string? about) => new(Reason, string.IsNullOrWhiteSpace(about) ? null : about, WhenText);
}

public sealed record FindingLine(string Reason, string? About, string When);

public sealed record StaffingVerdict(int Occurrences, IReadOnlyList<OccurrenceFinding> Blocks, IReadOnlyList<OccurrenceFinding> Warnings)
{
    public static StaffingVerdict None { get; } = new(0, [], []);

    public bool IsBlocked => Blocks.Count > 0;

    public int BlockedOccurrences => Blocks.Select(finding => finding.Occurrence).Distinct().Count();

    public int WarnedOccurrences => Warnings.Select(finding => finding.Occurrence).Distinct().Count();

    public IReadOnlyList<FindingGroup> BlockGroups => StaffingRules.Group(Blocks);

    public IReadOnlyList<FindingGroup> WarningGroups => StaffingRules.Group(Warnings);

    public string Summary => Occurrences == 0
        ? "No occurrences left in the period"
        : IsBlocked
            ? $"Blocked on {BlockedOccurrences} of {Occurrences} {(Occurrences == 1 ? "occurrence" : "occurrences")}"
            : Warnings.Count > 0
                ? $"Free, a warning on {WarnedOccurrences} of {Occurrences} {(Occurrences == 1 ? "occurrence" : "occurrences")}"
                : Occurrences == 1 ? "Free for its one occurrence" : $"Free for all {Occurrences} occurrences";

    public string? BlockedText => IsBlocked
        ? $"The staff member is not free for {BlockedOccurrences} of the job's {Occurrences} remaining {(Occurrences == 1 ? "occurrence" : "occurrences")}. {string.Join(". ", BlockGroups.Select(group => group.Text))}."
        : null;
}

public sealed record QualificationMatch(int Required, int Matching, IReadOnlyList<Guid> Missing, int Preferred = 0, int PreferredMatching = 0)
{
    public bool MeetsAll => Missing.Count == 0;
}

public sealed record RankFacts(Guid ResourceId, string Name, bool MeetsAllRequired, int MatchingQualifications, int HeldJobs, int PreferredMatching = 0);

public static class StaffingRules
{
    public const string OutsidePatternReason = "Outside the staff member's work pattern";

    public static (DateOnly From, DateOnly To)? RemainingRange(DateOnly periodStart, DateOnly periodEnd, DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var today = JobRules.LocalDay(new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)), zone);
        var from = periodStart > today ? periodStart : today;
        return periodEnd < from ? null : (from, periodEnd);
    }

    public static IReadOnlyList<OccurrenceInstant> RemainingOccurrences(JobTimes times, DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (RemainingRange(times.PeriodStart, times.PeriodEnd, nowUtc, zone) is not { } range)
        {
            return [];
        }

        return [.. JobRules.Occurrences(times, range.From, range.To, zone).Where(occurrence => occurrence.EndUtc > nowUtc)];
    }

    public static IReadOnlyList<OccurrenceInstant> RemainingOccurrences(IEnumerable<OccurrenceTimes> timetable, DateOnly periodStart, DateOnly periodEnd, DateTime nowUtc, TimeZoneInfo zone) =>
        RemainingOccurrences(JobTimes.Recurring(periodStart, periodEnd, timetable), nowUtc, zone);

    public static StaffingVerdict Judge(StaffAvailability staff, IReadOnlyList<OccurrenceInstant> occurrences, Guid? ownAssignmentId = null)
    {
        ArgumentNullException.ThrowIfNull(staff);
        ArgumentNullException.ThrowIfNull(occurrences);
        var blocks = staff.Blocks.Where(block => ownAssignmentId is null || block.SourceId != ownAssignmentId).ToList();
        var warnings = staff.Warnings.Where(warning => ownAssignmentId is null || warning.SourceId != ownAssignmentId).ToList();
        var blocked = new List<OccurrenceFinding>();
        var warned = new List<OccurrenceFinding>();

        var working = staff.OrderedWorking();
        foreach (var occurrence in occurrences)
        {
            var before = blocked.Count;
            foreach (var block in blocks)
            {
                if (Meets(block.StartUtc, block.EndUtc, occurrence))
                {
                    AddOnce(blocked, before, new OccurrenceFinding(occurrence, block.Reason, block.SourceId));
                }
            }

            var warnedBefore = warned.Count;
            foreach (var warning in warnings)
            {
                if (Meets(warning.StartUtc, warning.EndUtc, occurrence))
                {
                    AddOnce(warned, warnedBefore, new OccurrenceFinding(occurrence, warning.Reason, warning.SourceId));
                }
            }

            if (blocked.Count == before && !StaffAvailability.IsWithin(working, occurrence.StartUtc, occurrence.EndUtc))
            {
                warned.Add(new OccurrenceFinding(occurrence, OutsidePatternReason, null));
            }
        }

        return new StaffingVerdict(occurrences.Count, blocked, warned);
    }

    public static QualificationMatch Match(IEnumerable<Guid> required, IReadOnlySet<Guid> held)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(held);
        var asked = required.Distinct().ToList();
        var missing = asked.Where(qualification => !held.Contains(qualification)).ToList();
        return new QualificationMatch(asked.Count, asked.Count - missing.Count, missing);
    }

    public static QualificationMatch MatchAtLevels(IEnumerable<SlotRequirement> asked, IReadOnlySet<Guid> held)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(held);
        var rows = asked.ToList();
        var required = rows.Where(row => row.Level == RequirementLevel.Required).Select(row => row.QualificationId).Distinct().ToList();
        var preferred = rows.Where(row => row.Level == RequirementLevel.Preferred).Select(row => row.QualificationId).Distinct().Except(required).ToList();
        var match = Match(required, held);
        return match with { Preferred = preferred.Count, PreferredMatching = preferred.Count(held.Contains) };
    }

    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> candidates, Func<T, RankFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(facts);
        return [.. candidates
            .Select(candidate => (Candidate: candidate, Facts: facts(candidate)))
            .OrderByDescending(pair => pair.Facts.MeetsAllRequired)
            .ThenByDescending(pair => pair.Facts.MatchingQualifications)
            .ThenByDescending(pair => pair.Facts.PreferredMatching)
            .ThenBy(pair => pair.Facts.HeldJobs)
            .ThenBy(pair => pair.Facts.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(pair => pair.Facts.ResourceId)
            .Select(pair => pair.Candidate)];
    }

    public static IReadOnlyList<RankFacts> Rank(IEnumerable<RankFacts> facts) => Rank(facts, fact => fact);

    public static IReadOnlyList<FindingGroup> Group(IEnumerable<OccurrenceFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return [.. findings
            .GroupBy(finding => (finding.Reason, finding.SourceId))
            .Select(group => new FindingGroup(group.Key.Reason, group.Key.SourceId, group.Min(finding => finding.Occurrence.Date), group.Max(finding => finding.Occurrence.Date), group.Select(finding => finding.Occurrence).Distinct().Count()))
            .OrderBy(group => group.First)
            .ThenBy(group => group.Reason, StringComparer.Ordinal)
            .ThenBy(group => group.SourceId)];
    }

    private static bool Meets(DateTime startUtc, DateTime endUtc, OccurrenceInstant occurrence) => startUtc < occurrence.EndUtc && endUtc > occurrence.StartUtc;

    private static void AddOnce(List<OccurrenceFinding> findings, int from, OccurrenceFinding finding)
    {
        for (var index = from; index < findings.Count; index++)
        {
            if (findings[index].Reason == finding.Reason && findings[index].SourceId == finding.SourceId)
            {
                return;
            }
        }

        findings.Add(finding);
    }
}
