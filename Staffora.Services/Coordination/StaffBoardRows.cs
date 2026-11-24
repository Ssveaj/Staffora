using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Services.Coordination;

public sealed record BoardFilter(string? Name = null, bool NameDescending = false)
{
    public const int MaxNameLength = 100;

    public bool IsEmpty => NameText.Length == 0;

    public string NameText
    {
        get
        {
            var text = (Name ?? string.Empty).Trim();
            return text.Length > MaxNameLength ? text[..MaxNameLength] : text;
        }
    }
}

public sealed record BoardBand(TimeOnly Start, TimeOnly End)
{
    public string Text => $"{AbsenceRules.TimeText(Start)}-{AbsenceRules.TimeText(End)}";
}

public sealed record BoardAbsence(Guid AbsenceId, AbsenceKind Kind, ResourceAbsenceState State, TimeOnly? From, TimeOnly? Until)
{
    public bool Blocks => AbsenceRules.Blocks(Kind, State);

    public string Text => (Kind, State) switch
    {
        (AbsenceKind.SickLeave, ResourceAbsenceState.Approved) => "Sick leave, handled",
        (AbsenceKind.SickLeave, _) => "Sick leave, reported",
        (_, ResourceAbsenceState.Approved) => "Planned leave, approved",
        _ => "Planned leave, waiting for approval",
    };

    public string TimeText => (From, Until) switch
    {
        ({ } from, { } until) => $"{AbsenceRules.TimeText(from)}-{AbsenceRules.TimeText(until)}",
        ({ } from, null) => $"From {AbsenceRules.TimeText(from)}",
        (null, { } until) => $"Until {AbsenceRules.TimeText(until)}",
        _ => "All day",
    };

    public TimeOnly Start => From ?? TimeOnly.MinValue;

    public TimeOnly End => Until ?? TimeOnly.MinValue;
}

public sealed record BoardOccurrence(Guid AssignmentId, Guid JobId, string Job, string Slot, string Customer, SlotAssignmentState State, TimeOnly Start, TimeOnly End, bool AwaitsReply = false, string? Place = null, string? Occurrence = null)
{
    public string Times => $"{AbsenceRules.TimeText(Start)}-{AbsenceRules.TimeText(End)}";

    public string StateText => State switch
    {
        SlotAssignmentState.Assigned => "Assigned",
        SlotAssignmentState.Accepted => "Said yes",
        _ => "Asked",
    };
}

public sealed record BoardStaffDay(DateOnly Date, IReadOnlyList<BoardBand> Bands, IReadOnlyList<BoardAbsence> Absences)
{
    public IReadOnlyList<string> Closed { get; init; } = [];

    public bool IsClosed => Closed.Count > 0;

    public string ClosedText => string.Join(", ", Closed.Select(ClosedDayRules.BlockReason));
}

public sealed record BoardStaff(Guid ResourceId, Guid UserId, string Name, IReadOnlyList<BoardStaffDay> Days, bool IsReader);

public sealed class BoardStaffMembers
{
    internal BoardStaffMembers(
        Guid tenantId,
        DateOnly monday,
        DateOnly today,
        string timeZoneId,
        BoardFilter filter,
        IReadOnlyList<BoardStaff> staffMembers,
        bool mayAddAbsence,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> held,
        int page = 1,
        int totalStaffMembers = -1)
    {
        TenantId = tenantId;
        Monday = monday;
        Today = today;
        TimeZoneId = timeZoneId;
        Filter = filter;
        StaffMembers = staffMembers;
        MayAddAbsence = mayAddAbsence;
        Held = held;
        Page = page;
        TotalStaffMembers = totalStaffMembers < 0 ? staffMembers.Count : totalStaffMembers;
    }

    public int Page { get; }

    public int TotalStaffMembers { get; }

    public int PageCount => BoardRules.PageCount(TotalStaffMembers);

    public int FirstShown => StaffMembers.Count == 0 ? 0 : ((Page - 1) * BoardRules.PageSize) + 1;

    public DateOnly Monday { get; }

    public IReadOnlyList<DateOnly> Days => BoardRules.Days(Monday);

    public DateOnly Today { get; }

    public string TimeZoneId { get; }

    public BoardFilter Filter { get; }

    public IReadOnlyList<BoardStaff> StaffMembers { get; }

    public bool MayAddAbsence { get; }

    internal Guid TenantId { get; }

    internal IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> Held { get; }
}

public sealed record BoardSlotOccurrence(Guid SlotId, string Slot, Guid JobId, string Job, string Customer, PriorityKind Kind, DateOnly Date, TimeOnly Start, TimeOnly End, int Free, int Judged, string? Place = null, string? Occurrence = null)
{
    public string Times => $"{AbsenceRules.TimeText(Start)}-{AbsenceRules.TimeText(End)}";

    public string FreeText => Free == 0 ? $"None of {Judged} free" : $"{Free} of {Judged} free";
}

public sealed class BoardBookings
{
    private readonly IReadOnlyDictionary<(Guid Staff, DateOnly Date), IReadOnlyList<BoardOccurrence>> occurrences;
    private readonly IReadOnlyDictionary<(Guid Slot, Guid Staff), BoardPair> pairs;

    internal BoardBookings(DateOnly monday, BoardFilter filter, IReadOnlyDictionary<(Guid Staff, DateOnly Date), IReadOnlyList<BoardOccurrence>> occurrences, IReadOnlyList<BoardSlotOccurrence> toStaff, IReadOnlyDictionary<(Guid Slot, Guid Staff), BoardPair> pairs, int page = 1)
    {
        Monday = monday;
        Filter = filter;
        this.occurrences = occurrences;
        ToStaff = toStaff;
        this.pairs = pairs;
        Page = page;
    }

    public DateOnly Monday { get; }

    public int Page { get; }

    public BoardFilter Filter { get; }

    public IReadOnlyList<BoardSlotOccurrence> ToStaff { get; }

    public IReadOnlyList<BoardOccurrence> OccurrencesOf(Guid resourceId, DateOnly date) => this.occurrences.TryGetValue((resourceId, date), out var occurrences) ? occurrences : [];

    public BoardPair? Pair(Guid slotId, Guid resourceId) => this.pairs.TryGetValue((slotId, resourceId), out var pair) ? pair : null;
}
