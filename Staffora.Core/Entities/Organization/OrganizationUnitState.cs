using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Organization;

public enum OrganizationUnitState
{
    [Display(Name = "New")]
    [Description("Being set up: the unit is created when the first step is taken")]
    [Icon("add_business")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Active")]
    [Description("In use: it takes new role holdings, and the roles held on it grant their permissions")]
    [Icon("check_circle")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("Out of use for now: it takes no new role holdings, and the roles held on it grant nothing until it is reactivated")]
    [Icon("pause_circle")]
    [StateStyling("#FF9800")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Open)]
    Inactive = 2,

    [Display(Name = "Archived")]
    [Description("Kept for the record: out of use, with every unit below it archived too")]
    [Icon("archive")]
    [StateStyling("#757575")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Archived)]
    Archived = 3,

    [Display(Name = "Deleted")]
    [Description("Removed, but its history is kept")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    [DeleteState]
    Deleted = 4,
}
