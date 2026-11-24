using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Integration;

public enum IntegrationMessageState
{
    [Display(Name = "Pending")]
    Pending = 0,

    [Display(Name = "Delivered")]
    Delivered = 1,

    [Display(Name = "Dead letter")]
    DeadLettered = 2,
}
