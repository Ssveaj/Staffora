using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Jobs;

public class JobTimeSlot : TenantEntityBase
{
    public Guid JobId { get; set; }

    public Job Job { get; set; } = null!;

    [Display(Name = "Day")]
    public Weekday Weekday { get; set; } = Weekday.Monday;

    [Display(Name = "Start")]
    public TimeOnly Start { get; set; }

    [Display(Name = "End")]
    public TimeOnly End { get; set; }
}
