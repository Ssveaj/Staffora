using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Jobs;

public enum JobSlotState
{
    [Display(Name = "Create")]
    [Description("Being added: how many slots alike, their name and the qualifications they require")]
    [Icon("add")]
    [StateStyling("#9E9E9E")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = -1,

    [Display(Name = "Active")]
    [Description("Needed: the job is staffed with a staff member for it")]
    [Icon("person_search")]
    [StateStyling("#37DE57")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 0,

    [Display(Name = "Canceled")]
    [Description("No longer needed, and it is kept for the record")]
    [Icon("person_off")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Canceled)]
    Canceled = 1,

    [Display(Name = "Deleted")]
    [Description("Removed with its job, but its history is kept")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    [DeleteState]
    Deleted = 2,
}
