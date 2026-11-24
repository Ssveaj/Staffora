namespace Staffora.Core.Entities;

public abstract class TenantEntityBase : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTime Created { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime? Modified { get; set; }
    public Guid? ModifiedById { get; set; }
    public Guid? ImpersonatedById { get; set; }
}
