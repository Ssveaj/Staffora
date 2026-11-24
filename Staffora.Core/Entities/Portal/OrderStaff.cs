using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.Portal;

public class OrderStaff : TenantEntityBase
{
    public const int NameMaxLength = 201;

    public Guid OrderId { get; set; }

    public Order Order { get; set; } = null!;

    [Display(Name = "Staff")]
    public string Name { get; set; } = string.Empty;

    public int Position { get; set; }
}
