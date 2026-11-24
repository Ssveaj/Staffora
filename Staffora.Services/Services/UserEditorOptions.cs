using Staffora.Core.Models;

namespace Staffora.Services.Services;

public sealed record UserEditorOptions(bool FieldsLocked, bool CanManageRoles, RoleHoldingChoices Choices)
{
    public StaffSummary? Staff { get; init; }

    public bool CanManageQualifications { get; init; }

    public StaffSchedule? Schedule { get; init; }

    public string? ScheduleProblem { get; init; }

    public bool CanAssignPatterns { get; init; }

    public bool CanAddAbsence { get; init; }

    public bool OnConsole { get; init; }

    public static UserEditorOptions ReadOnly { get; } = new(true, false, RoleHoldingChoices.None);
}
