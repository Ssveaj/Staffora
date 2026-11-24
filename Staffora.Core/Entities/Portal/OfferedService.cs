using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Catalogue;

namespace Staffora.Core.Entities.Portal;

public class OfferedService : TenantEntityBase
{
    public const int NameMaxLength = Service.NameMaxLength;

    public const int DescriptionMaxLength = Service.DescriptionMaxLength;

    public Guid SupplierId { get; set; }

    public Supplier Supplier { get; set; } = null!;

    public Guid LinkedCrossTenantId { get; set; }

    [Display(Name = "Service")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Default number of people")]
    public int DefaultSlotCount { get; set; } = 1;

    public bool Offered { get; set; }
}
