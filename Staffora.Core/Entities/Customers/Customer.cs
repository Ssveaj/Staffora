using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Customers;

[CustomerWorkflow]
public class Customer : TenantStateEntityBase<CustomerState>
{
    public const int NameMaxLength = 200;

    public const int DescriptionMaxLength = 1000;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Portal tenant")]
    public Guid? LinkedTenantId { get; set; }

    [Display(Name = "Unit for portal jobs")]
    public Guid? DefaultUnitId { get; set; }

    public OrganizationUnit? DefaultUnit { get; set; }

    [Display(Name = "Contacts")]
    public ICollection<CustomerContact> Contacts { get; set; } = [];
}
