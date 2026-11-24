using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Organization;

[OrganizationUnitWorkflow]
public class OrganizationUnit : TenantStateEntityBase<OrganizationUnitState>
{
    public const int NameMaxLength = 200;

    public const int DescriptionMaxLength = 500;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Type")]
    public OrganizationUnitType UnitType { get; set; }

    [Display(Name = "Parent unit")]
    public Guid? ParentId { get; set; }

    public OrganizationUnit? Parent { get; set; }
    public ICollection<OrganizationUnit> Children { get; set; } = [];

    [Display(Name = "Custom fields")]
    public ICollection<OrganizationUnitAttribute> Attributes { get; set; } = [];

    public bool IsInUse => State == OrganizationUnitState.Active || UnitType == OrganizationUnitType.Company;
}
