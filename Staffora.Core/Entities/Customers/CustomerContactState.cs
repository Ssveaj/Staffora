using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Customers;

public enum CustomerContactState
{
    [Display(Name = "New")]
    [Description("Saved, not yet in use: jobs name it once it is activated")]
    [Icon("person_add")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Active")]
    [Description("In use: jobs of its customer name it")]
    [Icon("contact_mail")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("Out of use: named by nothing new and kept as it is until it is reactivated or deleted")]
    [Icon("pause_circle")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Inactive = 2,

    [Display(Name = "Deleted")]
    [Description("Removed, but its history is kept")]
    [Icon("delete")]
    [StateStyling("#F44336")]
    [ButtonStyle("Danger")]
    [EntityStateCategory(StateCategory.Canceled)]
    [DeleteState]
    Deleted = 3,
}
