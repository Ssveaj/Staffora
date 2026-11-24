using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Scheduling;

[ResourceAbsenceWorkflow]
public class ResourceAbsence : TenantStateEntityBase<ResourceAbsenceState>, ICommentedEntity
{
    [Display(Name = "Staff")]
    public Guid ResourceId { get; set; }

    public Resource Resource { get; set; } = null!;

    [Display(Name = "Kind")]
    public AbsenceKind Kind { get; set; }

    [Display(Name = "First day")]
    public DateOnly FirstDay { get; set; }

    [Display(Name = "From time")]
    public TimeOnly? StartTime { get; set; }

    [Display(Name = "Last day")]
    public DateOnly LastDay { get; set; }

    [Display(Name = "Until time")]
    public TimeOnly? EndTime { get; set; }
}
