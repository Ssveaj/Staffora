using System.ComponentModel.DataAnnotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Jobs;

[RequestRoundWorkflow]
public class RequestRound : TenantStateEntityBase<RequestRoundState>
{
    [Display(Name = "Slot")]
    public Guid JobSlotId { get; set; }

    public JobSlot Slot { get; set; } = null!;

    [Display(Name = "First to accept")]
    public bool FirstToAccept { get; set; }

    [Display(Name = "Answer by")]
    public DateTime? RespondBy { get; set; }

    [Display(Name = "Requests")]
    public ICollection<SlotAssignment> Requests { get; set; } = [];
}
