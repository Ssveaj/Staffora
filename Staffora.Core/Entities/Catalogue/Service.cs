using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Catalogue;

[ServiceWorkflow]
public class Service : TenantStateEntityBase<ServiceState>, IQualificationRequirements<ServiceQualification>
{
    public const int NameMaxLength = JobSlot.NameMaxLength;

    public const int DescriptionMaxLength = JobSlot.DescriptionMaxLength;

    public const int MinSlotCount = 1;

    public const int MaxSlotCount = Staffora.Core.Jobs.JobSlotRules.MaxAddCount;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Default number of people")]
    public int DefaultSlotCount { get; set; } = 1;

    [Display(Name = "Offered in the customer portal")]
    public bool OfferedInPortal { get; set; }

    public Guid CrossTenantId { get; set; }

    [Display(Name = "Qualifications")]
    public ICollection<ServiceQualification> Qualifications { get; set; } = [];

    public ServiceQualification NewRequirement(Guid qualificationId) => new() { TenantId = TenantId, ServiceId = Id, QualificationId = qualificationId };
}
