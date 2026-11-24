using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Resources;

public enum QualificationState
{
    [Display(Name = "New")]
    [Description("Being set up: the qualification is created when the first step is taken")]
    [Icon("add_task")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Active")]
    [Description("In the catalogue: it is given to staff members and asked for by slots")]
    [Icon("workspace_premium")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Archived")]
    [Description("Out of the catalogue: given to nobody new, but the staff members who hold it keep it on their record")]
    [Icon("archive")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Archived = 2,
}
