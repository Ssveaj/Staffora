using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Scheduling;

public enum ResourcePatternAssignmentState
{
    [Display(Name = "Registration")]
    [Description("Being prepared: the staff member works by it only once it is activated")]
    [Icon("pending_actions")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Registration = 0,

    [Display(Name = "Active")]
    [Description("In effect: the staff member works by the pattern on every date from its start to its end")]
    [Icon("event_available")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("Withdrawn: it gives the staff member no hours, but its history is kept")]
    [Icon("event_busy")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Inactive = 2,

    [Display(Name = "Deleted")]
    [Description("Removed before it took effect, but its history is kept")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    [DeleteState]
    Deleted = 3,
}
