using System.ComponentModel.DataAnnotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Tenancy;

[TenantRoleWorkflow]
public class TenantRole : TenantStateEntityBase<TenantRoleState>
{
    public const int NameMaxLength = 100;

    public const int DescriptionMaxLength = 500;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "System owner")]
    public bool IsSystemOwner { get; set; }

    [Display(Name = "Holders are staff members")]
    public bool ApplyResourceMembershipToUser { get; set; }

    public Tenant Tenant { get; set; } = null!;

    [Display(Name = "Permissions")]
    public ICollection<TenantRolePermission> Permissions { get; set; } = [];
}
