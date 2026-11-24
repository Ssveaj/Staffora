using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Catalogue;
using System.ComponentModel.DataAnnotations.Schema;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Jobs;

[JobSlotWorkflow]
public class JobSlot : TenantStateEntityBase<JobSlotState>, IQualificationRequirements<JobSlotQualification>
{
    public const int NameMaxLength = 100;

    public const int DescriptionMaxLength = 1000;

    [Display(Name = "Job")]
    public Guid JobId { get; set; }

    public Job Job { get; set; } = null!;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Service")]
    public Guid? ServiceId { get; set; }

    public Service? Service { get; set; }

    [Display(Name = "Required qualifications")]
    public ICollection<JobSlotQualification> Qualifications { get; set; } = [];

    [Display(Name = "Staff")]
    public ICollection<SlotAssignment> Assignments { get; set; } = [];

    public JobSlotQualification NewRequirement(Guid qualificationId) => new() { TenantId = TenantId, JobSlotId = Id, QualificationId = qualificationId };
    [NotMapped]
    [Display(Name = "How many")]
    public int Count { get; set; } = 1;

    [Display(Name = "Request rounds")]
    public ICollection<RequestRound> Rounds { get; set; } = [];
}
