using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Customers;

public enum CustomerState
{
    [Display(Name = "New")]
    [Description("Being set up: its details and contacts can be filled in, and it is ordered for once it is activated")]
    [Icon("add_business")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Active")]
    [Description("In use: jobs are registered for it and its active contacts")]
    [Icon("storefront")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("Out of use with its contacts: nothing new is registered for it, and it is kept as it is until it is reactivated or deleted")]
    [Icon("pause_circle")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Inactive = 2,

    [Display(Name = "Deleted")]
    [Description("Removed with its contacts, but its history is kept")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    [DeleteState]
    Deleted = 3,
}
