using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Scheduling;

[ResourcePatternAssignmentWorkflow]
public class ResourcePatternAssignment : TenantStateEntityBase<ResourcePatternAssignmentState>
{
    [Display(Name = "Staff")]
    public Guid ResourceId { get; set; }

    public Resource Resource { get; set; } = null!;

    [Display(Name = "Work pattern")]
    public Guid WorkPatternId { get; set; }

    public WorkPattern WorkPattern { get; set; } = null!;

    [Display(Name = "From")]
    public DateOnly EffectiveFrom { get; set; }

    [Display(Name = "To")]
    public DateOnly? EffectiveTo { get; set; }

    [Display(Name = "Cycle starts")]
    public DateOnly CycleAnchor { get; set; }
}
