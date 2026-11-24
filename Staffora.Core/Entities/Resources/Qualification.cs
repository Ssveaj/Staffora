using System.ComponentModel.DataAnnotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Resources;

[QualificationWorkflow]
public class Qualification : TenantStateEntityBase<QualificationState>
{
    public const int NameMaxLength = 100;

    public const int DescriptionMaxLength = 500;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }
}
