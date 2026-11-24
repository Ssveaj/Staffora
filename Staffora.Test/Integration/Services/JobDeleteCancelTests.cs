using Microsoft.EntityFrameworkCore;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.CustomFields;
using Staffora.Services.Scheduling;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class JobDeleteCancelTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public JobDeleteCancelTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_a_draft_job_removes_it_with_its_slots_occurrences_and_custom_values_and_frees_its_customer_and_unit()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Draft Delete", Ct);
        var coordinator = scenario.Coordinator;
        await using var host = scenario.HostOf(this.database, coordinator);
        var jobs = host.Get<JobService>();
        var room = await JobFieldAsync(scenario, "Room", JobFieldAudience.Coordinators);
        var registered = await jobs.CreateJobAsync(new Job
        {
            Name = "Draft algebra",
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            TimeSlots = [AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))],
            Attributes = [new JobAttribute { EntityAttributeDefinitionId = room, StringValue = "B12" }],
        }, JobWorkflowAttribute.RegisterStep, Ct);
        Assert.True(registered.IsSuccess, registered.Error);
        var job = registered.Value;
        var main = await SlotAsync(jobs, job, "Lead");
        var spare = await SlotAsync(jobs, job, "Spare");
        Assert.True((await AssignmentScenario.StepSlotAsync(jobs.Slots, spare, JobSlotWorkflowAttribute.CancelStep, Ct)).IsSuccess);
        await using (var reader = this.database.CreateSystemContext())
        {
            Assert.NotNull(await new JobsBlockCustomerDelete().FindUseAsync(reader, await CustomerAsync(scenario.Customer), Ct));
        }

        var deleted = await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.DeleteStep, Ct);
        Assert.True(deleted.IsSuccess, deleted.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.False(await system.Jobs.AnyAsync(row => row.Id == job, Ct));
        Assert.False(await system.JobSlots.AnyAsync(row => row.JobId == job, Ct));
        Assert.False(await system.Set<JobTimeSlot>().AnyAsync(row => row.JobId == job, Ct));
        Assert.False(await system.JobAttributes.AnyAsync(row => row.EntityId == job, Ct));
        var history = await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == job || entry.EntityId == main || entry.EntityId == spare).ToListAsync(Ct);
        var removal = history.Single(entry => entry.TransitionKey == JobWorkflowAttribute.DeleteStep);
        Assert.Equal(
            [JobSlotWorkflowAttribute.DeleteCanceledWithJobStep, JobSlotWorkflowAttribute.DeleteWithJobStep],
            history.Where(entry => entry.CorrelationId == removal.CorrelationId && entry.Id != removal.Id).Select(entry => entry.TransitionKey).Order());

        Assert.Null(await new JobsBlockCustomerDelete().FindUseAsync(system, await CustomerAsync(scenario.Customer), Ct));
        Assert.Null(await new JobsBlockUnitDelete().FindUseAsync(system, await system.OrganizationUnits.AsNoTracking().SingleAsync(unit => unit.Id == scenario.StaffMembers.RootId, Ct), Ct));
        Assert.Equal(JobService.NotFound, (await jobs.LoadJobUpdaterAsync(job, Ct)).Error);
    }

    [Fact]
    public async Task A_job_a_staff_member_was_asked_for_is_not_deleted_and_the_delete_needs_its_permission_and_registration()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Kept Job", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var registered = await jobs.CreateJobAsync(new Job
        {
            Name = "Kept algebra",
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            TimeSlots = [AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))],
        }, JobWorkflowAttribute.RegisterStep, Ct);
        var job = registered.Value;
        var slot = await SlotAsync(jobs, job, "Lead");

        await using (var writer = this.database.CreateSystemContext())
        {
            writer.SlotAssignments.Add(new SlotAssignment { TenantId = scenario.TenantId, JobSlotId = slot, ResourceId = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), State = SlotAssignmentState.Declined, CreatedById = scenario.Coordinator });
            await writer.SaveChangesAsync(Ct);
        }

        var refused = await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.DeleteStep, Ct);
        Assert.Equal(JobWorkflowAttribute.NoStaffAskedReason, refused.Error);
        var offered = (await host.Get<WorkflowService>().GetAvailableTransitionsAsync<JobState, Job>(job, JobWorkflowQueries.WithWorkflowGraph, Ct)).Value!;
        Assert.Contains(JobWorkflowAttribute.NoStaffAskedReason, offered.Single(answer => answer.Transition.Key == JobWorkflowAttribute.DeleteStep).BlockingReasons);
        Assert.True(await JobExistsAsync(job));

        var editor = await scenario.StaffMembers.AddPersonAsync(this.database, "Eddie", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, editor, Ct, RolePermission.JobsView, RolePermission.JobsCreate, RolePermission.JobsEdit);
        await using var editorHost = scenario.HostOf(this.database, editor);
        var other = await NeedlessDraftAsync(jobs, scenario, "Other draft");
        var notTheirs = await AssignmentScenario.StepJobAsync(editorHost.Get<JobService>(), other, JobWorkflowAttribute.DeleteStep, Ct);
        Assert.False(notTheirs.IsSuccess);
        Assert.True(await JobExistsAsync(other));

        Assert.DoesNotContain(WorkflowSteps(JobState.AwaitingCoordination), key => key == JobWorkflowAttribute.DeleteStep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceling_a_staffed_job_cancels_every_live_assignment_with_a_history_row_and_a_notice_each(bool byCustomer)
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, byCustomer ? "Customer Cancel" : "Supplier Cancel", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;
        var adaAssignment = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        var cyAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], cy, Ct));
        var deeAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], dee, Ct));
        Assert.True((await scenario.StepAsync(this.database, cy, cyAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.True((await scenario.StepAsync(this.database, dee, deeAssignment, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "Busy")).IsSuccess);
        await using var host = scenario.HostOf(this.database, coordinator);
        var jobs = host.Get<JobService>();
        Assert.True((await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        var before = new Dictionary<Guid, int>();
        foreach (var staff in new[] { ada, bo, cy, dee })
        {
            before[staff] = (await scenario.NoticesOfAsync(this.database, staff, Ct)).Count;
        }

        var step = byCustomer ? JobWorkflowAttribute.CustomerCancelStepFrom(JobState.Confirmed) : JobWorkflowAttribute.CancelStepFrom(JobState.Confirmed);
        var canceled = await AssignmentScenario.StepJobAsync(jobs, job, step, Ct);
        Assert.True(canceled.IsSuccess, canceled.Error);

        await using var system = this.database.CreateSystemContext();
        Assert.Equal(byCustomer ? JobState.CanceledByCustomer : JobState.CanceledBySupplier, (await system.Jobs.AsNoTracking().SingleAsync(row => row.Id == job, Ct)).State);
        Assert.All(await system.JobSlots.AsNoTracking().Where(row => row.JobId == job).ToListAsync(Ct), slot => Assert.Equal(JobSlotState.Canceled, slot.State));
        Assert.Equal([SlotAssignmentState.Canceled, SlotAssignmentState.Canceled, SlotAssignmentState.Canceled, SlotAssignmentState.Declined], await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment, cyAssignment, deeAssignment));

        var history = await system.StateHistoryEntries.AsNoTracking().ToListAsync(Ct);
        var jobRow = history.Single(entry => entry.EntityId == job && entry.TransitionKey == step);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelAssignedStep, history.Single(entry => entry.EntityId == adaAssignment && entry.CorrelationId == jobRow.CorrelationId).TransitionKey);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelRequestStep, history.Single(entry => entry.EntityId == boAssignment && entry.CorrelationId == jobRow.CorrelationId).TransitionKey);
        Assert.Equal(SlotAssignmentWorkflowAttribute.CancelAcceptedStep, history.Single(entry => entry.EntityId == cyAssignment && entry.CorrelationId == jobRow.CorrelationId).TransitionKey);
        Assert.DoesNotContain(history, entry => entry.EntityId == deeAssignment && entry.CorrelationId == jobRow.CorrelationId);
        Assert.Equal(2, history.Count(entry => slots.Contains(entry.EntityId) && entry.CorrelationId == jobRow.CorrelationId && entry.TransitionKey == JobSlotWorkflowAttribute.CancelStep));

        foreach (var (staff, assignment) in new[] { (ada, adaAssignment), (bo, boAssignment), (cy, cyAssignment) })
        {
            var notices = await scenario.NoticesOfAsync(this.database, staff, Ct);
            Assert.Equal(before[staff] + 1, notices.Count);
            Assert.Equal(("Algebra is canceled for you", $"?dlg=assignment-{assignment}"), (notices[0].Subject, notices[0].Link));
            Assert.DoesNotContain(new[] { "Ada", "Bo ", "Cy ", "Dee" }.Where(name => !notices[0].Text.StartsWith($"Hello {name.Trim()}", StringComparison.Ordinal)), name => notices[0].Text.Contains(name.Trim() + " Example", StringComparison.Ordinal));
        }

        Assert.Equal(before[dee], (await scenario.NoticesOfAsync(this.database, dee, Ct)).Count);

        var availability = await host.Get<StaffAvailabilityService>().ReadAsync([await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct)], PeriodStart, PeriodEnd, Ct);
        Assert.True(availability.IsSuccess, availability.Error);
        Assert.DoesNotContain(availability.Value!.StaffMembers.Values.SelectMany(staff => staff.Blocks), block => block.SourceId == adaAssignment);
        Assert.DoesNotContain(availability.Value!.StaffMembers.Values.SelectMany(staff => staff.Warnings), warning => warning.SourceId == adaAssignment);
        Assert.Equal(JobCoverage.Uncovered, (await jobs.QueryJobsAsync(new ListQuery(), Ct)).Value!.Rows.Single(row => row.Id == job).Coverage);
    }

    [Fact]
    public async Task Canceling_a_job_judges_every_step_it_takes_so_a_holder_of_JobsCancel_alone_cancels_no_job_with_slots()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Cancel Gate", Ct);
        var (job, _) = await scenario.StartedJobAsync(this.database, "Geometry", PeriodStart, PeriodEnd, Ct);
        var canceller = await scenario.StaffMembers.AddPersonAsync(this.database, "Cass", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, canceller, Ct, RolePermission.JobsView, RolePermission.JobsCancel);

        await using var host = scenario.HostOf(this.database, canceller);
        var refused = await AssignmentScenario.StepJobAsync(host.Get<JobService>(), job, JobWorkflowAttribute.CancelStepFrom(JobState.Coordinating), Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Cancel slot", refused.Error, StringComparison.Ordinal);
        Assert.Equal(JobState.Coordinating, (await StoredJobAsync(job)).State);
    }

    private static IEnumerable<string> WorkflowSteps(JobState state) =>
        Staffora.Core.Workflow.WorkflowRegistry.Default.Get<JobState, Job>().Transitions.Where(transition => transition.From == state).Select(transition => transition.Key);

    private async Task<Guid> JobFieldAsync(AssignmentScenario scenario, string name, JobFieldAudience audience)
    {
        var onboarded = scenario.StaffMembers.Onboarded;
        await using var console = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime());
        var created = await console.Get<CustomFieldService>().CreateFieldAsync(CustomFieldTarget.Job, new JobAttributeDefinition { State = CustomFieldState.Active, Name = name, ValueType = AttributeValueType.Text, Audience = audience }, onboarded.TenantId);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Guid> SlotAsync(JobService jobs, Guid job, string name)
    {
        var added = await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = name }, JobSlotWorkflowAttribute.AddStep, Ct);
        Assert.True(added.IsSuccess, added.Error);
        return added.Value;
    }

    private async Task<Guid> NeedlessDraftAsync(JobService jobs, AssignmentScenario scenario, string name)
    {
        var registered = await jobs.CreateJobAsync(new Job
        {
            Name = name,
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            TimeSlots = [AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(9, 0))],
        }, JobWorkflowAttribute.RegisterStep, Ct);
        Assert.True(registered.IsSuccess, registered.Error);
        return registered.Value;
    }

    private async Task<Staffora.Core.Entities.Customers.Customer> CustomerAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Customers.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<bool> JobExistsAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Jobs.AnyAsync(row => row.Id == id, Ct);
    }

    private async Task<Job> StoredJobAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Jobs.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private static Guid Made(Staffora.Core.Models.Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
