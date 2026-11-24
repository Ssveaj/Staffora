using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class JobCancelWorkflowTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private static Workflow<JobState, Job> Jobs => WorkflowRegistry.Default.Get<JobState, Job>();

    private static Workflow<JobSlotState, JobSlot> Slots => WorkflowRegistry.Default.Get<JobSlotState, JobSlot>();

    [Fact]
    public async Task A_job_in_registration_is_deleted_with_its_slots_by_their_own_steps_and_one_a_staff_member_was_asked_for_is_kept()
    {
        var job = Job(JobState.Registration);
        var active = Slot(job, JobSlotState.Active);
        var canceled = Slot(job, JobSlotState.Canceled);
        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.DeleteStep, Session()));

        var outcome = await Jobs.ExecuteTransitionAsync(job, JobWorkflowAttribute.DeleteStep, Session(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((JobState.Deleted, JobSlotState.Deleted, JobSlotState.Deleted), (job.State, active.State, canceled.State));
        Assert.Equal(
            [JobWorkflowAttribute.DeleteStep, JobSlotWorkflowAttribute.DeleteWithJobStep, JobSlotWorkflowAttribute.DeleteCanceledWithJobStep],
            outcome.Steps.Select(step => step.TransitionKey));
        Assert.All(Slots.Transitions.Where(transition => transition.To == JobSlotState.Deleted), transition => Assert.True(transition.IsSystemDriven));

        var asked = Job(JobState.Registration);
        var slot = Slot(asked, JobSlotState.Active);
        Assignment(slot, SlotAssignmentState.Declined);
        Assert.Equal([JobWorkflowAttribute.NoStaffAskedReason], Jobs.GetBlockingReasons(asked, JobWorkflowAttribute.DeleteStep, Session()));

        Assert.Equal([JobState.Registration], Jobs.Transitions.Where(transition => transition.To == JobState.Deleted).Select(transition => transition.From));
    }

    [Theory]
    [InlineData(JobState.Registration, JobState.CanceledBySupplier)]
    [InlineData(JobState.Coordinating, JobState.CanceledByCustomer)]
    [InlineData(JobState.Confirmed, JobState.CanceledBySupplier)]
    [InlineData(JobState.InProgress, JobState.CanceledByCustomer)]
    public async Task Canceling_a_job_cancels_its_active_slots_and_their_live_assignments_each_staff_member_told(JobState from, JobState to)
    {
        var job = Job(from);
        var slot = Slot(job, JobSlotState.Active);
        var done = Slot(job, JobSlotState.Canceled);
        var staffed = JobRules.StaffingOpen(from);
        var live = staffed ? new[] { Assignment(slot, SlotAssignmentState.Requested), Assignment(slot, SlotAssignmentState.Accepted), Assignment(slot, SlotAssignmentState.Assigned) } : [];
        var declined = staffed ? Assignment(slot, SlotAssignmentState.Declined) : null;
        var step = to == JobState.CanceledBySupplier ? JobWorkflowAttribute.CancelStepFrom(from) : JobWorkflowAttribute.CustomerCancelStepFrom(from);

        Assert.Equal(JobRules.SlotsOpen(from), Slots.GetBlockingReasons(slot, JobSlotWorkflowAttribute.CancelStep, Session()).Count == 0);
        Assert.Empty(Jobs.GetBlockingReasons(job, step, Session()));

        var outcome = await Jobs.ExecuteTransitionAsync(job, step, Session(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((to, JobSlotState.Canceled, JobSlotState.Canceled), (job.State, slot.State, done.State));
        Assert.All(live, assignment => Assert.Equal(SlotAssignmentState.Canceled, assignment.State));
        Assert.Equal(SlotAssignmentState.Declined, declined?.State ?? SlotAssignmentState.Declined);
        Assert.Equal([job.Id, slot.Id, .. live.Select(assignment => assignment.Id)], outcome.Steps.Select(taken => taken.Entity.Id));
        Assert.Equal(
            staffed ? new[] { SlotAssignmentWorkflowAttribute.CancelRequestStep, SlotAssignmentWorkflowAttribute.CancelAcceptedStep, SlotAssignmentWorkflowAttribute.CancelAssignedStep } : [],
            outcome.Steps.Where(taken => taken.Entity is SlotAssignment).Select(taken => taken.TransitionKey));

        Assert.Equal(
            live.Select(assignment => (MessageTypes.AssignmentCanceled.Key, assignment.Id, assignment.Resource.UserId)),
            outcome.OutgoingMessages.Select(message => (message.MessageType, message.EntityId, Assert.Single(message.RecipientUserIds))));
        Assert.All(outcome.OutgoingMessages, message => Assert.Contains("Algebra", message.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public void A_completed_or_canceled_job_is_canceled_no_more_and_its_custom_fields_are_locked()
    {
        foreach (var over in new[] { JobState.Completed, JobState.CanceledByCustomer, JobState.CanceledBySupplier })
        {
            Assert.DoesNotContain(Jobs.Transitions, transition => transition.From == over && !transition.IsSystemDriven);
            Assert.Equal(Staffora.Core.Workflow.FieldAccess.FieldAccessFlags.ReadOnly, Jobs.GetFieldConfig(over).GetFieldAccess(nameof(Staffora.Core.Entities.Jobs.Job.Attributes)));
        }

        foreach (var open in new[] { JobState.Create, JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress })
        {
            Assert.Equal(Staffora.Core.Workflow.FieldAccess.FieldAccessFlags.ReadWrite, Jobs.GetFieldConfig(open).GetFieldAccess(nameof(Staffora.Core.Entities.Jobs.Job.Attributes)));
        }
    }

    [Fact]
    public void A_required_job_field_holds_back_handing_over_starting_and_confirming_until_it_is_filled()
    {
        var room = new JobAttributeDefinition { State = CustomFieldState.Active, Id = Guid.NewGuid(), TenantId = Tenant, Name = "Room", ValueType = AttributeValueType.Text, IsRequired = true };
        var job = Job(JobState.Registration);
        Slot(job, JobSlotState.Active);
        var stored = new StoredJobReferences(job.CustomerId, job.ContactId, job.OrganizationUnitId);
        foreach (var step in new[] { JobWorkflowAttribute.HandOverStep, JobWorkflowAttribute.StartFromRegistrationStep })
        {
            Assert.Equal(["Room is required"], Jobs.GetBlockingReasons(job, step, Session(stored, room)));
        }

        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveRegistrationStep, Session(stored, room)));
        job.Attributes.Add(new JobAttribute { EntityAttributeDefinitionId = room.Id, StringValue = "B12" });
        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.HandOverStep, Session(stored, room)));

        var workflow = (IWorkflow)Jobs;
        Assert.True(workflow.RequiresCustomFieldsIn(JobState.AwaitingCoordination, typeof(JobAttributeDefinition)));
        Assert.True(workflow.RequiresCustomFieldsIn(JobState.Coordinating, typeof(JobAttributeDefinition)));
        Assert.True(workflow.RequiresCustomFieldsIn(JobState.Confirmed, typeof(JobAttributeDefinition)));
        Assert.False(workflow.RequiresCustomFieldsIn(JobState.Registration, typeof(JobAttributeDefinition)));
    }

    private static Job Job(JobState state)
    {
        var customer = new Customer { Id = Guid.NewGuid(), TenantId = Tenant, State = CustomerState.Active, Name = "Brightpath Learning" };
        var unit = new OrganizationUnit { Id = Guid.NewGuid(), TenantId = Tenant, State = OrganizationUnitState.Active, Name = "Branch East", UnitType = OrganizationUnitType.Branch };
        return new Job
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            State = state,
            Name = "Algebra",
            CustomerId = customer.Id,
            Customer = customer,
            OrganizationUnitId = unit.Id,
            OrganizationUnit = unit,
            PeriodStart = new DateOnly(2027, 1, 11),
            PeriodEnd = new DateOnly(2027, 5, 28),
            TimeSlots = [new JobTimeSlot { Weekday = Weekday.Monday, Start = new TimeOnly(8, 0), End = new TimeOnly(9, 0) }],
        };
    }

    private static JobSlot Slot(Job job, JobSlotState state)
    {
        var slot = new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, JobId = job.Id, Job = job, Name = "Lead", State = state };
        job.Slots.Add(slot);
        return slot;
    }

    private static SlotAssignment Assignment(JobSlot slot, SlotAssignmentState state)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), TenantId = Tenant, FirstName = "Ada", LastName = "Example", State = UserState.Active };
        var resource = new Resource { Id = Guid.NewGuid(), TenantId = Tenant, UserId = user.Id, User = user, State = ResourceState.Active };
        var assignment = new SlotAssignment { Id = Guid.NewGuid(), TenantId = Tenant, JobSlotId = slot.Id, Slot = slot, ResourceId = resource.Id, Resource = resource, State = state };
        slot.Assignments.Add(assignment);
        return assignment;
    }

    private static WorkflowEnvironment Session(StoredJobReferences? stored = null, params IEntityAttributeDefinition[] fields) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Services(new RuntimeExecutionContext(Tenant, Actor), stored), (_, _) => null, CustomFieldCatalog.Of(typeof(JobAttributeDefinition), fields));

    private sealed class Services : IServiceProvider, IJobGuard
    {
        private readonly IRuntimeExecutionContext runtime;
        private readonly StoredJobReferences? stored;

        public Services(IRuntimeExecutionContext runtime, StoredJobReferences? stored)
        {
            this.runtime = runtime;
            this.stored = stored;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRuntimeExecutionContext) ? this.runtime
            : serviceType == typeof(IJobGuard) ? this
            : null;

        public StoredJobReferences StoredReferences(Job job) => this.stored ?? throw new InvalidOperationException("Nothing was read about the job.");

        public TimeZoneInfo TimeZoneOf(Job job) => throw new InvalidOperationException("No time zone was read for the job.");

        public IReadOnlyList<string> HolderClashes(Job job) => [];
    }
}
