using System.ComponentModel.DataAnnotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Portal;

[SupplierWorkflow]
public class Supplier : TenantStateEntityBase<SupplierState>
{
    public const int NameMaxLength = 200;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Staffing company")]
    public Guid LinkedTenantId { get; set; }
}
