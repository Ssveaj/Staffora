namespace Staffora.Core.Entities.Resources;

public class ResourceQualification : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid ResourceId { get; set; }

    public Guid QualificationId { get; set; }

    public Resource Resource { get; set; } = null!;

    public Qualification Qualification { get; set; } = null!;

    public DateTime Created { get; set; }

    public Guid CreatedById { get; set; }

    public DateTime? Modified { get; set; }

    public Guid? ModifiedById { get; set; }

    public Guid? ImpersonatedById { get; set; }
}
