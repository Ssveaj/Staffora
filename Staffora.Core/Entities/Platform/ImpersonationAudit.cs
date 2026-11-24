namespace Staffora.Core.Entities.Platform;

public class ImpersonationAudit
{
    public Guid Id { get; set; }

    public Guid ImpersonatorId { get; set; }

    public Guid TargetUserId { get; set; }

    public Guid TargetTenantId { get; set; }

    public DateTime Started { get; set; }

    public DateTime? Ended { get; set; }
}
