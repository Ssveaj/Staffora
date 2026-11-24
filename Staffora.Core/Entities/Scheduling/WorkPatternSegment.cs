using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Scheduling;

public class WorkPatternSegment : TenantEntityBase
{
    public Guid WorkPatternId { get; set; }

    public WorkPattern WorkPattern { get; set; } = null!;

    [Display(Name = "Day")]
    public int DayIndex { get; set; }

    [Display(Name = "Start")]
    public TimeOnly Start { get; set; }

    [Display(Name = "End")]
    public TimeOnly End { get; set; }
}
