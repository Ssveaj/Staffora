using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Jobs;

public enum JobSchedule
{
    [Display(Name = "Single")]
    Single = 1,

    [Display(Name = "Recurring")]
    Recurring = 2,
}
