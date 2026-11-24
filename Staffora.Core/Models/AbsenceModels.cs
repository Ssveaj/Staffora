using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;

namespace Staffora.Core.Models;

public sealed record AbsenceRow(Guid Id, Guid UserId, string Staff, AbsenceKind Kind, ResourceAbsenceState State, DateOnly FirstDay, TimeOnly? StartTime, DateOnly LastDay, TimeOnly? EndTime)
{
    public string KindLabel => Kind == AbsenceKind.SickLeave ? "Sick leave" : "Planned leave";

    public string PeriodLabel => AbsenceRules.PeriodText(FirstDay, StartTime, LastDay, EndTime) is var text && text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    public bool Blocks => AbsenceRules.Blocks(Kind, State);

    public string BlocksLabel => Blocks ? "Blocks staffing" : "Does not block";

    public string Label => $"{KindLabel} of {Staff} {AbsenceRules.PeriodText(FirstDay, StartTime, LastDay, EndTime)}";
}

public sealed record AbsenceKindOption(AbsenceKind Value, string Label)
{
    public static IReadOnlyList<AbsenceKindOption> All { get; } = [new(AbsenceKind.PlannedLeave, "Planned leave"), new(AbsenceKind.SickLeave, "Sick leave")];
}

public sealed record AbsenceEditorOptions(bool FieldsLocked, string Staff, string? Note);

public sealed record OwnAbsenceStatus(DateOnly Today, IReadOnlyList<AbsenceRow> Ahead, int AheadCount, int WaitingCount)
{
    public AbsenceRow? Current => Ahead.FirstOrDefault(absence => absence.FirstDay <= Today);

    public string Summary => Current is { } now
        ? $"{now.KindLabel} today, {AbsenceRules.OwnStatusText(now.State)}, until {ScheduleRules.DateText(now.LastDay)}"
        : Ahead.Count > 0
            ? $"Next: {Ahead[0].KindLabel.ToLowerInvariant()} {AbsenceRules.PeriodText(Ahead[0].FirstDay, Ahead[0].StartTime, Ahead[0].LastDay, Ahead[0].EndTime)}"
            : "No absence ahead";
}
