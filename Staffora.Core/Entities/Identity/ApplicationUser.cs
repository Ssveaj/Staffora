using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Identity;

[ApplicationUserWorkflow]
public class ApplicationUser : IdentityUser<Guid>, ITenantEntity, IStateEntity<UserState>
{
    [Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;

    [Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;

    public UserState State { get; set; }

    public DateTime? LastSignedIn { get; set; }

    public bool IsInTransition { get; set; }

    public bool IsSystemAccount { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public DateTime Created { get; set; }

    public Guid CreatedById { get; set; }

    public DateTime? Modified { get; set; }

    public Guid? ModifiedById { get; set; }

    public Guid? ImpersonatedById { get; set; }

    public ICollection<ApplicationUserAttribute> Attributes { get; set; } = [];
}
