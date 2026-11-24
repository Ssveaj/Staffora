using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Portal;

public enum OrderState
{
    [Display(Name = "New")]
    [Description("Being written: nothing is stored until it is saved")]
    [Icon("post_add")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Draft")]
    [Description("Saved and not sent yet: it is completed and then sent to the staffing company")]
    [Icon("edit_note")]
    [StateStyling("#03A9F4")]
    [EntityStateCategory(StateCategory.Entry)]
    Registration = 1,

    [Display(Name = "Sent")]
    [Description("Sent to the staffing company: it waits for a coordinator there to staff the job")]
    [Icon("send")]
    [StateStyling("#FF9800")]
    [EntityStateCategory(StateCategory.Open)]
    Sent = 2,

    [Display(Name = "Being staffed")]
    [Description("The staffing company's coordinator is staffing the job")]
    [Icon("edit_calendar")]
    [StateStyling("#9C27B0")]
    [EntityStateCategory(StateCategory.Open)]
    Coordinating = 3,

    [Display(Name = "Confirmed")]
    [Description("The staffing company confirmed the job: its staff members are named")]
    [Icon("event_available")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Confirmed = 4,

    [Display(Name = "In progress")]
    [Description("The job's period has started")]
    [Icon("play_circle")]
    [StateStyling("#009688")]
    [EntityStateCategory(StateCategory.Open)]
    InProgress = 5,

    [Display(Name = "Completed")]
    [Description("The job's period has ended, and the order is kept for the record")]
    [Icon("task_alt")]
    [StateStyling("#607D8B")]
    [EntityStateCategory(StateCategory.Done)]
    Completed = 6,

    [Display(Name = "Canceled by us")]
    [Description("Your organization canceled the order, and it is kept for the record")]
    [Icon("cancel")]
    [StateStyling("#795548")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    CanceledByUs = 7,

    [Display(Name = "Canceled by the staffing company")]
    [Description("The staffing company canceled the confirmed job, and the order is kept for the record")]
    [Icon("event_busy")]
    [StateStyling("#795548")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    CanceledBySupplier = 8,

    [Display(Name = "Declined")]
    [Description("The staffing company declined the order before confirming it, and it is kept for the record")]
    [Icon("block")]
    [StateStyling("#795548")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    DeclinedBySupplier = 9,

    [Display(Name = "Cancellation sent")]
    [Description("Your organization canceled the order and the staffing company has not answered yet: it reads canceled once the staffing company has canceled the job, or completed when the job was completed already")]
    [Icon("schedule_send")]
    [StateStyling("#FF7043")]
    [EntityStateCategory(StateCategory.Open)]
    CancellationSent = 10,
}
