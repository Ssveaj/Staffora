using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Catalogue;

public enum ServiceState
{
    [Display(Name = "New")]
    [Description("Being set up: the service is created when the first step is taken")]
    [Icon("add_business")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Active")]
    [Description("In the catalogue: slots are made from it")]
    [Icon("design_services")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Archived")]
    [Description("Out of the catalogue: no new slot is made from it, but slots made from it keep what they took")]
    [Icon("archive")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Archived = 2,
}
