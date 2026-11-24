using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Scheduling;

public enum AbsenceKind
{
    [Display(Name = "Planned leave")]
    [Description("Leave planned ahead, which blocks the staff member once an approver has approved it")]
    [Icon("beach_access")]
    PlannedLeave = 0,

    [Display(Name = "Sick leave")]
    [Description("Illness, which blocks the staff member from the moment it is reported")]
    [Icon("sick")]
    SickLeave = 1,
}
