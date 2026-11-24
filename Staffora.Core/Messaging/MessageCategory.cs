using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;

namespace Staffora.Core.Messaging;

public enum MessageCategory
{
    [Display(Name = "Account")]
    [Description("Your account and its access")]
    [Icon("manage_accounts")]
    Account = 0,

    [Display(Name = "Staffing")]
    [Description("Requests and assignments of staff members")]
    [Icon("assignment_ind")]
    Staffing = 1,

    [Display(Name = "Absence")]
    [Description("Absence reported or registered")]
    [Icon("event_busy")]
    Absence = 2,

    [Display(Name = "Ordering")]
    [Description("Jobs ordered by customers")]
    [Icon("storefront")]
    Ordering = 3,

    [Display(Name = "Conversation")]
    [Description("New comments in your conversations")]
    [Icon("forum")]
    Conversation = 4,
}
