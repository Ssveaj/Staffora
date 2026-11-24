using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Resources;

public enum ResourceState
{
    [Display(Name = "Registration")]
    [Description("A staff member whose account is not active yet: not staffable until it is")]
    [Icon("pending")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Registration = 0,

    [Display(Name = "Active")]
    [Description("A staff member in service: a candidate for slots on the units they belong to")]
    [Icon("badge")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("No longer a staff member, or their account is deactivated: not staffable, but their history is kept")]
    [Icon("person_off")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Inactive = 2,
}
