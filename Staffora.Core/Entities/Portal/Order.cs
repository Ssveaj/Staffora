using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Portal;

[OrderWorkflow]
public class Order : TenantStateEntityBase<OrderState>, ICommentedEntity
{
    public const int NameMaxLength = Job.NameMaxLength;

    public const int DescriptionMaxLength = Job.DescriptionMaxLength;

    [Display(Name = "Job")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Period starts")]
    public DateOnly PeriodStart { get; set; }

    [Display(Name = "Period ends")]
    public DateOnly PeriodEnd { get; set; }

    [Display(Name = "Schedule")]
    public JobSchedule Schedule { get; set; } = JobSchedule.Recurring;

    [Display(Name = "Starts")]
    public TimeOnly? StartTime { get; set; }

    [Display(Name = "Ends")]
    public TimeOnly? EndTime { get; set; }

    [Display(Name = "Place")]
    public string? PlaceName { get; set; }

    [Display(Name = "Street address")]
    public string? StreetAddress { get; set; }

    [Display(Name = "Postal code")]
    public string? PostalCode { get; set; }

    [Display(Name = "City")]
    public string? City { get; set; }

    [Display(Name = "Supplier")]
    public Guid SupplierId { get; set; }

    public Supplier Supplier { get; set; } = null!;

    [Display(Name = "Orderer")]
    public Guid OrdererId { get; set; }

    public ApplicationUser Orderer { get; set; } = null!;

    [Display(Name = "Cross-tenant id")]
    public Guid? CrossTenantId { get; set; }

    [Display(Name = "Job link")]
    public Guid? LinkedCrossTenantId { get; set; }

    [Display(Name = "People")]
    public ICollection<OrderLine> Lines { get; set; } = [];
}
