using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Jobs;

public enum SlotAssignmentState
{
    [Display(Name = "Create")]
    [Description("Being made: a request or an assignment is its first step")]
    [Icon("add")]
    [StateStyling("#9E9E9E")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Requested")]
    [Description("The staff member is asked and has not answered yet")]
    [Icon("help")]
    [StateStyling("#FF9800")]
    [EntityStateCategory(StateCategory.Open)]
    Requested = 1,

    [Display(Name = "Accepted")]
    [Description("The staff member said yes, and the coordinator picks who works")]
    [Icon("thumb_up")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Open)]
    Accepted = 2,

    [Display(Name = "Declined")]
    [Description("The staff member said no, or took back their yes")]
    [Icon("thumb_down")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    Declined = 3,

    [Display(Name = "Assigned")]
    [Description("The staff member works the job for this slot")]
    [Icon("how_to_reg")]
    [StateStyling("#37DE57")]
    [EntityStateCategory(StateCategory.Done)]
    Assigned = 4,

    [Display(Name = "Not selected")]
    [Description("Another staff member was assigned to the slot")]
    [Icon("person_remove")]
    [StateStyling("#9E9E9E")]
    [EntityStateCategory(StateCategory.Canceled)]
    NotSelected = 5,

    [Display(Name = "Canceled")]
    [Description("Withdrawn by the coordinator or with its slot, and kept for the record")]
    [Icon("cancel")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    Canceled = 6,

    [Display(Name = "Question to the coordinator")]
    [Description("The staff member asked the coordinator a question about the request and waits for the answer. The request stays open")]
    [Icon("contact_support")]
    [StateStyling("#5E35B1")]
    [EntityStateCategory(StateCategory.Open)]
    StaffQuestion = 7,

    [Display(Name = "Expired")]
    [Description("Nobody answered before the answer-by time, so the request closed by itself")]
    [Icon("timer_off")]
    [StateStyling("#9E9E9E")]
    [EntityStateCategory(StateCategory.Canceled)]
    Expired = 8,
}
