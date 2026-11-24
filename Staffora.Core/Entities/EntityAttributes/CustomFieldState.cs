using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.EntityAttributes;

public enum CustomFieldState
{
    [Display(Name = "New")]
    [Description("Being set up: what the field applies to, its tenant and its type are chosen, and it is saved as a draft")]
    [Icon("add_task")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = -1,

    [Display(Name = "Draft")]
    [Description("Saved and still being prepared: no tenant sees it, and no record carries or needs it until it is activated")]
    [Icon("edit_note")]
    [StateStyling("#8E7CC3")]
    [EntityStateCategory(StateCategory.Entry)]
    Draft = 0,

    [Display(Name = "Active")]
    [Description("In use: it shows in the dialogs of the records it applies to, and a required field is needed there")]
    [Icon("check_circle")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("Out of use: it is hidden from every record's dialog and required nowhere. The values records hold are kept and return when it is activated again")]
    [Icon("visibility_off")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Inactive = 2,
}
