using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Messaging;

public enum OutgoingMessageStatus
{
    [Display(Name = "Pending")]
    Pending = 0,

    [Display(Name = "Delivered")]
    Delivered = 1,

    [Display(Name = "Not delivered")]
    Failed = 2,
}
