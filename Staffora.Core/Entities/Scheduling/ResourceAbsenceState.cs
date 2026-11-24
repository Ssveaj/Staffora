using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Scheduling;

public enum ResourceAbsenceState
{
    [Display(Name = "Requested")]
    [Description("Reported and waiting for an approver: a sick leave blocks the staff member already, a planned leave only once approved")]
    [Icon("pending_actions")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Open)]
    [EntryState]
    Requested = 0,

    [Display(Name = "Approved")]
    [Description("Approved: the staff member is absent and is not staffed for these days")]
    [Icon("event_busy")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Done)]
    Approved = 1,

    [Display(Name = "Rejected")]
    [Description("Rejected by an approver: it blocks nothing, but its history is kept")]
    [Icon("block")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    Rejected = 2,

    [Display(Name = "Canceled")]
    [Description("Withdrawn by the staff member or a coordinator: it blocks nothing, but its history is kept")]
    [Icon("event_available")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    Canceled = 3,

    [Display(Name = "Question to the staff member")]
    [Description("A coordinator asked the staff member a question and waits for the answer. It counts as requested")]
    [Icon("help")]
    [StateStyling("#8E24AA")]
    [EntityStateCategory(StateCategory.Open)]
    Question = 4,

    [Display(Name = "Question to the coordinator")]
    [Description("The staff member asked the coordinators a question and waits for the answer. It counts as requested")]
    [Icon("contact_support")]
    [StateStyling("#5E35B1")]
    [EntityStateCategory(StateCategory.Open)]
    StaffQuestion = 5,
}
