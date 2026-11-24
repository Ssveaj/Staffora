using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Jobs;

public enum RequestRoundState
{
    [Display(Name = "Create")]
    [Description("Being sent: the round and its requests are made in one step")]
    [Icon("add")]
    [StateStyling("#9E9E9E")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Open")]
    [Description("Sent: its requests may still be answered")]
    [Icon("campaign")]
    [StateStyling("#FF9800")]
    [EntityStateCategory(StateCategory.Open)]
    Open = 1,

    [Display(Name = "Filled")]
    [Description("Every slot of its slot group got a holder")]
    [Icon("task_alt")]
    [StateStyling("#37DE57")]
    [EntityStateCategory(StateCategory.Done)]
    Filled = 2,

    [Display(Name = "Closed")]
    [Description("Closed by the coordinator, and its unanswered requests were canceled")]
    [Icon("cancel")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    Closed = 3,

    [Display(Name = "Expired")]
    [Description("The answer-by time passed: its unanswered requests expired, and yeses waiting for a pick stay")]
    [Icon("timer_off")]
    [StateStyling("#9E9E9E")]
    [EntityStateCategory(StateCategory.Canceled)]
    Expired = 4,
}
