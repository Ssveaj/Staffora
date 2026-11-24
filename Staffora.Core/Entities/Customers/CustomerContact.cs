using System.ComponentModel.DataAnnotations;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Customers;

[CustomerContactWorkflow]
public class CustomerContact : TenantStateEntityBase<CustomerContactState>
{
    public const int NameMaxLength = 100;

    public const int EmailMaxLength = 256;

    public const int PhoneMaxLength = 50;

    public const int TitleMaxLength = 100;

    [Display(Name = "Customer")]
    public Guid CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    [Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;

    [Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;

    [Display(Name = "Email")]
    public string? Email { get; set; }

    [Display(Name = "Phone")]
    public string? Phone { get; set; }

    [Display(Name = "Title")]
    public string? Title { get; set; }

    [Display(Name = "Portal user")]
    public Guid? LinkedUserId { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}
