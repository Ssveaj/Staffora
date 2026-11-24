using System.ComponentModel.DataAnnotations;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.Jobs;

[JobWorkflow]
public class Job : TenantStateEntityBase<JobState>, ICommentedEntity
{
    public const int NameMaxLength = 200;

    public const int DescriptionMaxLength = 2000;

    public const int PlaceNameMaxLength = 200;

    public const int StreetAddressMaxLength = 200;

    public const int PostalCodeMaxLength = 20;

    public const int CityMaxLength = 100;

    public const int AccessNotesMaxLength = 1000;

    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Description")]
    public string? Description { get; set; }

    [Display(Name = "Customer")]
    public Guid CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    [Display(Name = "Contact")]
    public Guid? ContactId { get; set; }

    public CustomerContact? Contact { get; set; }

    [Display(Name = "Unit")]
    public Guid OrganizationUnitId { get; set; }

    public OrganizationUnit OrganizationUnit { get; set; } = null!;

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

    [Display(Name = "Access notes")]
    public string? AccessNotes { get; set; }

    [Display(Name = "Coordinator")]
    public Guid? CoordinatorUserId { get; set; }

    public ApplicationUser? Coordinator { get; set; }

    [Display(Name = "Cross-tenant id")]
    public Guid? CrossTenantId { get; set; }

    [Display(Name = "Customer request id")]
    public Guid? LinkedCrossTenantId { get; set; }

    [Display(Name = "Timetable")]
    public ICollection<JobTimeSlot> TimeSlots { get; set; } = [];

    [Display(Name = "Slots")]
    public ICollection<JobSlot> Slots { get; set; } = [];

    [Display(Name = "Custom fields")]
    public ICollection<JobAttribute> Attributes { get; set; } = [];
}
