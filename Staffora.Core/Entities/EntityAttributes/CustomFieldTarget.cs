using System.ComponentModel.DataAnnotations;

namespace Staffora.Core.Entities.EntityAttributes;

public enum CustomFieldTarget
{
    [Display(Name = "Users")]
    User = 0,

    [Display(Name = "Organization units")]
    OrganizationUnit = 1,

    [Display(Name = "Jobs")]
    Job = 2,
}
