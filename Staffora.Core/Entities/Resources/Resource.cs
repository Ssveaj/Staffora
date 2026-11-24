using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Resources;

[ResourceWorkflow]
public class Resource : TenantStateEntityBase<ResourceState>
{
    [Display(Name = "User")]
    public Guid UserId { get; set; }

    public ApplicationUser User { get; set; } = null!;

    [Display(Name = "Units")]
    public ICollection<ResourceUnitMembership> Memberships { get; set; } = [];
}
