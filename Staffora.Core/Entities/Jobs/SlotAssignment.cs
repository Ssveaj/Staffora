using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Jobs;

[SlotAssignmentWorkflow]
public class SlotAssignment : TenantStateEntityBase<SlotAssignmentState>, ICommentedEntity
{
    [Display(Name = "Slot")]
    public Guid JobSlotId { get; set; }

    public JobSlot Slot { get; set; } = null!;

    [Display(Name = "Staff")]
    public Guid ResourceId { get; set; }

    public Resource Resource { get; set; } = null!;

    [Display(Name = "Request round")]
    public Guid? RequestRoundId { get; set; }

    public RequestRound? Round { get; set; }

    [Display(Name = "Answer by")]
    public DateTime? RespondBy { get; set; }

    public DateTime? LastStaffCommentAt { get; set; }

    public DateTime? LastCoordinatorCommentAt { get; set; }

    public DateTime? StaffSeenAt { get; set; }
}
