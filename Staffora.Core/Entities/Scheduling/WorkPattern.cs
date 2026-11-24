using System.ComponentModel.DataAnnotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Scheduling;

[WorkPatternWorkflow]
public class WorkPattern : TenantStateEntityBase<WorkPatternState>
{
    public const int NameMaxLength = 100;

    public const int DescriptionMaxLength = 500;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Cycle length (weeks)")]
    public int CycleWeeks { get; set; } = 1;

    [Display(Name = "Working hours")]
    public ICollection<WorkPatternSegment> Segments { get; set; } = [];
}
