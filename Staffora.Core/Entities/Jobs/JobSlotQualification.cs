using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Resources;

namespace Staffora.Core.Entities.Jobs;

public class JobSlotQualification : ITenantEntity, IQualificationRequirement
{
    public Guid TenantId { get; set; }

    public Guid JobSlotId { get; set; }

    public Guid QualificationId { get; set; }

    public RequirementLevel Level { get; set; }

    public JobSlot JobSlot { get; set; } = null!;

    public Qualification Qualification { get; set; } = null!;

    public DateTime Created { get; set; }

    public Guid CreatedById { get; set; }

    public DateTime? Modified { get; set; }

    public Guid? ModifiedById { get; set; }

    public Guid? ImpersonatedById { get; set; }
}
