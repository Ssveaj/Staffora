using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Jobs;

public enum JobState
{
    [Display(Name = "New")]
    [Description("Being registered: its customer, period and timetable are filled in, and nothing is stored until it is registered")]
    [Icon("add_circle")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Registration")]
    [Description("Registered: its details, timetable and slots are completed before it is handed over for coordination")]
    [Icon("edit_note")]
    [StateStyling("#03A9F4")]
    [EntityStateCategory(StateCategory.Entry)]
    Registration = 1,

    [Display(Name = "Awaiting coordination")]
    [Description("Handed over: it waits for a coordinator to start staffing it")]
    [Icon("pending_actions")]
    [StateStyling("#FF9800")]
    [EntityStateCategory(StateCategory.Open)]
    AwaitingCoordination = 2,

    [Display(Name = "Coordinating")]
    [Description("Being staffed: its coordinator requests and assigns staff members for its slots")]
    [Icon("edit_calendar")]
    [StateStyling("#9C27B0")]
    [EntityStateCategory(StateCategory.Open)]
    Coordinating = 3,

    [Display(Name = "Confirmed")]
    [Description("Confirmed by its coordinator: its timetable is fixed and it is worked as staffed")]
    [Icon("event_available")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Confirmed = 4,

    [Display(Name = "In progress")]
    [Description("Its period has started")]
    [Icon("play_circle")]
    [StateStyling("#009688")]
    [EntityStateCategory(StateCategory.Open)]
    InProgress = 5,

    [Display(Name = "Completed")]
    [Description("Its period has ended, and it is kept for the record")]
    [Icon("task_alt")]
    [StateStyling("#607D8B")]
    [EntityStateCategory(StateCategory.Done)]
    Completed = 6,

    [Display(Name = "Canceled by the customer")]
    [Description("The customer canceled it, and it is kept for the record")]
    [Icon("event_busy")]
    [StateStyling("#795548")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    CanceledByCustomer = 7,

    [Display(Name = "Canceled by us")]
    [Description("We canceled it, and it is kept for the record")]
    [Icon("cancel")]
    [StateStyling("#795548")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    CanceledBySupplier = 8,

    [Display(Name = "Deleted")]
    [Description("Removed before it was staffed, but its history is kept")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    [DeleteState]
    Deleted = 9,
}
