using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Entities.Portal;

public enum SupplierState
{
    [Display(Name = "New")]
    [Description("Being linked: nothing is sent to it yet")]
    [Icon("add_business")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Entry)]
    [EntryState]
    Create = 0,

    [Display(Name = "Active")]
    [Description("The staffing company the organization orders staff members from")]
    [Icon("handshake")]
    [StateStyling("#37DE57")]
    [ButtonStyle("Success")]
    [EntityStateCategory(StateCategory.Open)]
    Active = 1,

    [Display(Name = "Inactive")]
    [Description("The staffing company takes no new orders from the organization for now")]
    [Icon("pause_circle")]
    [StateStyling("#757575")]
    [ButtonStyle("Warning")]
    [EntityStateCategory(StateCategory.Archived)]
    Inactive = 2,
}
