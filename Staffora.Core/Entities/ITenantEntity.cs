namespace Staffora.Core.Entities;

public interface ITenantEntity
{
    Guid TenantId { get; set; }

    DateTime Created { get; set; }

    Guid CreatedById { get; set; }

    DateTime? Modified { get; set; }

    Guid? ModifiedById { get; set; }

    Guid? ImpersonatedById { get; set; }
}
