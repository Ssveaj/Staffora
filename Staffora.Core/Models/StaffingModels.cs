using Staffora.Core.Entities.Jobs;
using Staffora.Core.Staffing;

namespace Staffora.Core.Models;

public sealed record StaffingCandidateRow(
    Guid SlotId,
    Guid ResourceId,
    int Rank,
    string Name,
    QualificationMatch Match,
    IReadOnlyList<string> Missing,
    int HeldJobs,
    StaffingVerdict Verdict,
    IReadOnlyList<string> Blocks,
    IReadOnlyList<string> Warnings,
    SlotAssignmentState? Earlier = null)
{
    public string MatchLabel => (Match.Required == 0
        ? Match.Preferred == 0 ? "No qualification required" : "None required"
        : Match.MeetsAll
            ? Match.Required == 1 ? "The required one" : $"All {Match.Required} required"
            : $"{Match.Matching} of {Match.Required} required (missing: {string.Join(", ", Missing)})")
        + (Match.Preferred == 0 ? string.Empty : $", {Match.PreferredMatching} of {Match.Preferred} preferred");

    public string HeldLabel => HeldJobs switch
    {
        0 => "None",
        1 => "1 job",
        _ => $"{HeldJobs} jobs",
    };

    public string AvailabilityLabel => Verdict.Summary;

    public string? EarlierLabel => Earlier switch
    {
        SlotAssignmentState.Declined => "Declined before",
        SlotAssignmentState.NotSelected => "Not selected before",
        SlotAssignmentState.Canceled => "Canceled before",
        SlotAssignmentState.Expired => "No answer before",
        _ => null,
    };

    public IReadOnlyList<FindingLine> BlockLines { get; init; } = [];

    public IReadOnlyList<FindingLine> WarningLines { get; init; } = [];

    public IReadOnlyList<QualificationMark> Marks { get; init; } = [];
}

public sealed record QualificationMark(string Name, bool Preferred, bool Held);

public sealed record RequiredQualification(string Name, bool Preferred, bool Archived);

public sealed record SlotHolder(Guid AssignmentId, string Name, bool Assigned);

public sealed record SlotAsked(Guid AssignmentId, string Name, string? AnswerBy);

public sealed record SlotStaffingSummary(string Job, string Period, string Timetable, int RemainingOccurrences, IReadOnlyList<string> Required, bool MayStaff, string? Reason)
{
    public int SlotCount { get; init; } = 1;

    public int Held { get; init; }

    public bool Full { get; init; }

    public IReadOnlyList<RequiredQualification> Qualifications { get; init; } = [];

    public IReadOnlyList<SlotHolder> Holders { get; init; } = [];

    public IReadOnlyList<SlotAsked> Asked { get; init; } = [];

    public int OpenAgain { get; init; }

    public string StaffedLabel => SlotCount > 1
        ? OpenAgain > 0 ? $"{Staffora.Core.Jobs.SlotGroups.HeldText(Held, SlotCount)} held, {OpenAgain} open again" : $"{Staffora.Core.Jobs.SlotGroups.HeldText(Held, SlotCount)} held"
        : Full ? "Filled" : Held > 0 ? "Said yes, not assigned yet" : OpenAgain > 0 ? "Open again" : "Open";


    public string RemainingLabel => RemainingOccurrences switch
    {
        0 => "No occurrences left in the period",
        1 => "1 occurrence left",
        _ => $"{RemainingOccurrences} occurrences left",
    };
}
