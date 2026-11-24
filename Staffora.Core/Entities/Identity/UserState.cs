using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Identity;

public enum UserState
{
    [Display(Name = "New")]
    [Description("Being set up: the user is created when the first step is taken")]
    [Icon("person_add")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Active")]
    [Description("Signs in, can be impersonated by the platform owner, and the roles held grant their permissions")]
    [Icon("check_circle")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("Deactivated: cannot sign in, is not impersonated, takes no new roles, and the roles held grant nothing until the user is reactivated")]
    [Icon("person_off")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Inactive = 2,
}
