using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Organization;

namespace Staffora.Core.Entities.Scheduling;

public class ClosedDay : TenantEntityBase
{
    public const int NameMaxLength = 100;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "First day")]
    public DateOnly FirstDay { get; set; }

    [Display(Name = "Last day")]
    public DateOnly LastDay { get; set; }

    [Display(Name = "Unit")]
    public Guid? OrganizationUnitId { get; set; }

    public OrganizationUnit? OrganizationUnit { get; set; }
}
