using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Models;

public sealed record JobListItem(Guid Id, string Name, string Customer, DateOnly PeriodStart, DateOnly PeriodEnd, string Unit, string? Coordinator, JobState State, int ActiveSlots, JobCoverage Coverage)
{
    public string PeriodLabel => Times is { } times ? JobRules.WhenText(times) : JobRules.PeriodText(PeriodStart, PeriodEnd);

    public JobTimes? Times { get; init; }

    public string CoverageLabel => ActiveSlots == 0
        ? "No slots yet"
        : $"{JobCoverageRules.Label(Coverage)} ({ActiveSlots} {(ActiveSlots == 1 ? "slot" : "slots")})";
}

public sealed record JobSlotRow(Guid Id, Guid JobId, string Name, string? Description, IReadOnlyList<string> Qualifications, JobSlotState State, JobCoverage Coverage, string? Holder = null, int Requested = 0, int Accepted = 0)
{
    public string QualificationsLabel => Qualifications.Count == 0 ? "None" : string.Join(", ", Qualifications);

    public string CoverageLabel => State != JobSlotState.Active ? "—" : SlotCount > 1 ? HeldLabel : JobCoverageRules.Label(Coverage);

    public int SlotCount { get; init; } = 1;

    public int Held { get; init; }

    public IReadOnlyList<string> Holders { get; init; } = [];

    public string HeldLabel => SlotGroups.HeldText(Held, SlotCount);

    public bool HolderOutOfService { get; init; }

    public string StaffMembersLabel => State != JobSlotState.Active
        ? "—"
        : SlotCount > 1
        ? string.Join(", ", new[] { Holders.Count > 0 ? $"{string.Join(", ", Holders)} (assigned)" : null, Requested > 0 ? $"{Requested} asked" : null, Accepted > 0 ? $"{Accepted} said yes" : null }.OfType<string>()) is { Length: > 0 } staff ? staff : "Nobody asked yet"
        : Holder is { } holder
        ? HolderOutOfService ? $"{holder} (assigned, out of service)" : $"{holder} (assigned)"
        : Requested + Accepted == 0
            ? "Nobody asked yet"
            : string.Join(", ", new[] { Requested > 0 ? $"{Requested} asked" : null, Accepted > 0 ? $"{Accepted} said yes" : null }.OfType<string>());
}

public sealed record SlotAssignmentRow(Guid Id, Guid JobSlotId, string Staff, SlotAssignmentState State, DateTime Created);

public sealed record OwnAssignmentRow(Guid Id, string Job, string Slot, string Customer, DateOnly PeriodStart, DateOnly PeriodEnd, SlotAssignmentState State, DateTime Created, bool HasUnseenReply = false)
{
    public string Label => $"{Job}: {Slot}";

    public string? Place { get; init; }

    public string PeriodLabel => Times is { } times ? JobRules.WhenText(times) : JobRules.PeriodText(PeriodStart, PeriodEnd);

    public JobTimes? Times { get; init; }

    public DateTime? RespondBy { get; init; }

    public string? AnswerBy { get; init; }

    public string NextLabel => NextLabelOf(State);

    public string NextLabelWithDeadline => AnswerBy is { } answerBy ? $"{NextLabel}. {answerBy}" : NextLabel;

    public static string? NextHintOf(SlotAssignmentState state) => state switch
    {
        SlotAssignmentState.Requested => "Read what the job contains. If something is unclear, write a question to the coordinator before you accept or decline.",
        SlotAssignmentState.StaffQuestion => "The coordinator answers in the conversation. You can write another question, or accept or decline now.",
        SlotAssignmentState.Accepted => "The coordinator picks who works among the staff members who said yes. You can take back your yes until then.",
        SlotAssignmentState.Assigned => "The job is yours. Write to the coordinator in the conversation if something comes up.",
        _ => null,
    };

    public static string NextLabelOf(SlotAssignmentState state) => state switch
    {
        SlotAssignmentState.Requested => "Your answer is needed",
        SlotAssignmentState.StaffQuestion => "Your question waits for the coordinator",
        SlotAssignmentState.Accepted => "You said yes, and the coordinator picks who works",
        SlotAssignmentState.Assigned => "You are assigned",
        SlotAssignmentState.Declined => "You said no",
        SlotAssignmentState.NotSelected => "Another staff member was assigned",
        SlotAssignmentState.Canceled => "Canceled",
        SlotAssignmentState.Expired => "The time to answer passed",
        _ => string.Empty,
    };
}

public sealed record OwnWork(IReadOnlyList<OwnAssignmentRow> Requests, int RequestCount, IReadOnlyList<OwnAssignmentRow> Next, int NextCount)
{
    public const int Shown = 5;

    public IReadOnlyList<OwnAssignmentRow> Unread { get; init; } = [];

    public int UnreadCount { get; init; }
}

public sealed record StaffConversationRow(Guid AssignmentId, string Staff, string Slot, SlotAssignmentState State, bool AwaitsReply, int Count, DateTimeOffset? LastAt, string? LastAuthor, string? LastText)
{
    public const int ExcerptLength = 120;

    public static string Excerpt(string text)
    {
        var trimmed = text.Trim().ReplaceLineEndings(" ");
        if (trimmed.Length <= ExcerptLength)
        {
            return trimmed;
        }

        var cut = trimmed[..(ExcerptLength - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > ExcerptLength / 2 ? cut[..space] : cut).TrimEnd() + "…";
    }
}

public sealed record JobCoverageSummary(int ActiveSlots, JobCoverage Coverage);

public sealed record JobStaffEntry(Guid SlotId, string Name, IReadOnlyList<string> Assigned, int Open, IReadOnlyList<string> Asked, IReadOnlyList<string> SaidYes)
{
    public int OpenAgain { get; init; }

    public string? Waiting => string.Join(" · ", new[] { Asked.Count > 0 ? $"Asked: {string.Join(", ", Asked)}" : null, SaidYes.Count > 0 ? $"Said yes: {string.Join(", ", SaidYes)}" : null }.OfType<string>()) is { Length: > 0 } text ? text : null;
}

public sealed record AssignmentJobField(Guid Id, string Name, string Value, bool IsGlobal, string? Audience = null);
