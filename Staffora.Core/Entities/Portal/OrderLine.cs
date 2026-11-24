using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Portal;

public class OrderLine : ITenantEntity
{
    public const int MinCount = 1;

    public const int MaxCount = Staffora.Core.Jobs.JobSlotRules.MaxAddCount;

    public const int MaxLines = 10;

    public Guid TenantId { get; set; }

    public Guid OrderId { get; set; }

    public Guid OfferedServiceId { get; set; }

    [Display(Name = "People")]
    public int Count { get; set; } = 1;

    public Order Order { get; set; } = null!;

    public OfferedService OfferedService { get; set; } = null!;

    public DateTime Created { get; set; }

    public Guid CreatedById { get; set; }

    public DateTime? Modified { get; set; }

    public Guid? ModifiedById { get; set; }

    public Guid? ImpersonatedById { get; set; }
}
