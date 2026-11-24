using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Scheduling;

public enum WorkPatternState
{
    [Display(Name = "Draft")]
    [Description("Being edited: its days and hours can change, and it is assigned to nobody until it is published")]
    [Icon("edit_calendar")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Draft = 0,

    [Display(Name = "Published")]
    [Description("Ready for use: it can be assigned to staff members and no longer changes. Copy it to a new draft to change it")]
    [Icon("event_available")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Published = 1,

    [Display(Name = "Archived")]
    [Description("Out of use: assigned to nobody new, but staff members who have it keep it")]
    [Icon("archive")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Archived = 2,

    [Display(Name = "Deleted")]
    [Description("Removed with its segments, but its history is kept")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    [DeleteState]
    Deleted = 3,
}
