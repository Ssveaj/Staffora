using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Lists;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.CustomFields;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class JobCustomFieldTests : IClassFixture<RealDatabase>
{
    private const string Secret = "SECRET-4200";

    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public JobCustomFieldTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_staff_member_reads_only_the_job_fields_whose_audience_includes_them_and_only_once_assigned_by_every_path()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Field Audience", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var room = await FieldAsync(scenario, "Room", JobFieldAudience.AssignedStaff);
        var budget = await FieldAsync(scenario, "Budget", JobFieldAudience.Coordinators);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var coordinator = scenario.Coordinator;
        await SetValuesAsync(scenario, job, JobWorkflowAttribute.SaveCoordinatingStep, (room, "B12"), (budget, Secret));

        var adaAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        Assert.Empty(await FieldsAsync(scenario, ada, adaAssignment));
        Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Assert.Empty(await FieldsAsync(scenario, ada, adaAssignment));

        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct)).IsSuccess);
        Assert.Equal([("Room", "B12", (string?)null)], (await FieldsAsync(scenario, ada, adaAssignment)).Select(field => (field.Name, field.Value, field.Audience)));

        Assert.Equal(
            [("Budget", Secret, (string?)"Coordinators only"), ("Room", "B12", "Also the assigned staff member")],
            (await FieldsAsync(scenario, coordinator, adaAssignment)).Select(field => (field.Name, field.Value, field.Audience)));
        Assert.Empty(await FieldsAsync(scenario, bo, boAssignment));
        await using (var boHost = scenario.HostOf(this.database, bo))
        {
            Assert.Equal(SlotAssignmentService.NotFound, (await boHost.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(adaAssignment, Ct)).Error);
        }

        await using (var host = scenario.HostOf(this.database, ada))
        {
            var jobs = host.Get<JobService>();
            var attributes = host.Get<CustomFieldService>();
            var assignments = host.Get<SlotAssignmentService>();
            Assert.Equal(JobService.NoView, (await jobs.LoadJobUpdaterAsync(job, Ct)).Error);
            Assert.Equal(JobService.NoView, (await jobs.GetJobCustomFieldsAsync(job, Ct)).Error);
            Assert.Equal(JobService.NoView, (await jobs.GetJobHistoryAsync(job, new StateHistoryQuery(20), Ct)).Error);
            Assert.False((await jobs.QueryJobsAsync(new ListQuery(), Ct)).IsSuccess);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.GetDefinitionsAsync<JobAttributeDefinition>()).Error);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.QueryDefinitionsAsync(new ListQuery())).Error);
            Assert.Equal(CustomFieldService.NoViewPermission, (await attributes.LoadFieldAsync(CustomFieldTarget.Job, budget, Ct)).Error);
            Assert.Equal([("Room", "B12")], (await attributes.ReadAssignedStaffJobFieldsAsync(adaAssignment, Ct)).Value!.Select(field => (field.Field.Name, field.Value?.StringValue)));

            var load = (await assignments.LoadSlotAssignmentUpdaterAsync(adaAssignment, Ct)).Value!;
            var history = await assignments.GetSlotAssignmentHistoryAsync(adaAssignment, new StateHistoryQuery(20), Ct);
            Assert.Equal(StateHistoryService.OwnersOnly, history.Error);
            var threads = await assignments.Conversations.GetSlotAssignmentCommentsAsync(adaAssignment, Ct);
            foreach (var read in new object?[] { load.Entity, history.Value, threads.Value, await scenario.NoticesOfAsync(this.database, ada, Ct) })
            {
                var text = Json(read);
                Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
                Assert.DoesNotContain("Budget", text, StringComparison.Ordinal);
                Assert.DoesNotContain(budget.ToString(), text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Empty(load.Entity.Slot.Job.Attributes);
        }

        Assert.True((await scenario.StepAsync(this.database, coordinator, adaAssignment, SlotAssignmentWorkflowAttribute.CancelAssignedStep, Ct)).IsSuccess);
        Assert.Empty(await FieldsAsync(scenario, ada, adaAssignment));
    }

    [Fact]
    public async Task The_audience_is_judged_in_the_query_that_reads_the_values_so_a_field_narrowed_elsewhere_is_never_read_and_only_the_assigned_staff_member_reads_any()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Field Query", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var room = await FieldAsync(scenario, "Room", JobFieldAudience.AssignedStaff);
        var budget = await FieldAsync(scenario, "Budget", JobFieldAudience.Coordinators);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        await SetValuesAsync(scenario, job, JobWorkflowAttribute.SaveCoordinatingStep, (room, "B12"), (budget, Secret));
        var adaAssignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        var boAssignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[1], bo, Ct));

        await using var staff = scenario.HostOf(this.database, ada);
        var read = (await staff.Get<CustomFieldService>().ReadAssignedStaffJobFieldsAsync(adaAssignment, Ct)).Value!;
        Assert.Equal([(room, "B12")], read.Select(field => (field.Field.Id, field.Value?.StringValue)));
        Assert.DoesNotContain(read, field => field.Field.Id == budget || field.Value?.StringValue == Secret);
        Assert.Equal(["Room"], (await staff.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(adaAssignment, Ct)).Value!.JobFields.Select(field => field.Name));

        await using (var asked = scenario.HostOf(this.database, bo))
        {
            var attributes = asked.Get<CustomFieldService>();
            Assert.Empty((await attributes.ReadAssignedStaffJobFieldsAsync(boAssignment, Ct)).Value!);
            Assert.Empty((await attributes.ReadAssignedStaffJobFieldsAsync(adaAssignment, Ct)).Value!);
        }

        await using (var console = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime()))
        {
            var attributes = console.Get<CustomFieldService>();
            Assert.True((await attributes.StepFieldAsync(CustomFieldTarget.Job, room, CustomFieldSteps.SaveActive, field => ((JobAttributeDefinition)field).Audience = JobFieldAudience.Coordinators, Ct)).IsSuccess);
        }

        Assert.Empty((await staff.Get<CustomFieldService>().ReadAssignedStaffJobFieldsAsync(adaAssignment, Ct)).Value!);
        Assert.Empty((await staff.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(adaAssignment, Ct)).Value!.JobFields);
    }

    [Fact]
    public async Task A_job_value_in_a_locked_custom_fields_area_is_refused_on_save_however_it_is_written()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Locked Fields", Ct);
        var room = await FieldAsync(scenario, "Room", JobFieldAudience.AssignedStaff);
        var (job, _) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct);
        await SetValuesAsync(scenario, job, JobWorkflowAttribute.SaveCoordinatingStep, (room, "B12"));

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        Assert.True((await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        await SetValuesAsync(scenario, job, JobWorkflowAttribute.SaveConfirmedStep, (room, "C3"));
        Assert.Equal("C3", await ValueAsync(job, room));

        Assert.True((await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.CancelStepFrom(JobState.Confirmed), Ct)).IsSuccess);
        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        updater.Entity.Attributes.Single().StringValue = "D4";
        var saved = await host.Get<WorkflowService>().SaveEntityAsync(updater, Ct);
        Assert.False(saved.IsSuccess);
        Assert.Equal("Custom fields cannot be changed while the Job is CanceledBySupplier.", saved.Error);

        var added = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        added.Entity.Attributes.Clear();
        Assert.False((await host.Get<WorkflowService>().SaveEntityAsync(added, Ct)).IsSuccess);

        await using (var context = this.database.CreateContext(scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator)))
        {
            var stored = await context.JobAttributes.SingleAsync(value => value.EntityId == job, Ct);
            stored.StringValue = "E5";
            await Assert.ThrowsAsync<Staffora.Data.WorkflowFieldEditException>(() => context.SaveChangesAsync(Ct));
        }

        Assert.Equal("C3", await ValueAsync(job, room));
    }

    [Fact]
    public async Task A_required_job_field_is_needed_before_the_hand_over_and_required_once_handed_over()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Required Field", Ct);
        var room = await FieldAsync(scenario, "Room", JobFieldAudience.Coordinators, required: true);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var registered = await jobs.CreateJobAsync(new Job
        {
            Name = "Chemistry",
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            TimeSlots = [AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))],
        }, JobWorkflowAttribute.RegisterStep, Ct);
        Assert.True(registered.IsSuccess, registered.Error);
        var job = registered.Value;
        Assert.True((await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Lead" }, JobSlotWorkflowAttribute.AddStep, Ct)).IsSuccess);

        Assert.Equal("Room is required", (await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.HandOverStep, Ct)).Error);
        await SetValuesAsync(scenario, job, JobWorkflowAttribute.HandOverStep, (room, "B12"));
        Assert.Equal(JobState.AwaitingCoordination, (await StoredAsync(job)).State);

        var emptied = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        emptied.Entity.Attributes.Single().StringValue = " ";
        var refused = await jobs.UpdateJobAndTransitionAsync(emptied, JobWorkflowAttribute.SaveAwaitingStep, null, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Room", refused.Error, StringComparison.Ordinal);
        Assert.Equal("B12", await ValueAsync(job, room));
    }

    [Fact]
    public async Task The_custom_fields_panel_shows_job_fields_with_their_audience_and_a_deactivated_field_keeps_its_values()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Field Panel", Ct);
        var room = await FieldAsync(scenario, "Room", JobFieldAudience.Coordinators);
        var (job, _) = await scenario.StartedJobAsync(this.database, "Drama", PeriodStart, PeriodEnd, Ct);
        await SetValuesAsync(scenario, job, JobWorkflowAttribute.SaveCoordinatingStep, (room, "Stage"));

        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        await using var console = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), ServiceHost.SharingLookupCacheOf(owner));
        var attributes = owner.Get<CustomFieldService>();
        var platformFields = console.Get<CustomFieldService>();
        var listed = (await attributes.QueryDefinitionsAsync(new ListQuery { Filters = new Dictionary<string, string> { [CustomFieldList.Entity] = CustomFieldList.Job } })).Value!;
        var row = Assert.Single(listed.Rows);
        Assert.Equal((CustomFieldTarget.Job, "Room", "Coordinators only", false), (row.Target, row.Name, row.Audience, row.CanEdit));

        Assert.True((await platformFields.StepFieldAsync(CustomFieldTarget.Job, room, CustomFieldSteps.SaveActive, field => ((JobAttributeDefinition)field).Audience = JobFieldAudience.AssignedStaff, Ct)).IsSuccess);
        Assert.Equal("Also the assigned staff member", Assert.Single((await attributes.QueryDefinitionsAsync(new ListQuery())).Value!.Rows, field => field.Id == room).Audience);
        Assert.Equal(JobFieldAudience.AssignedStaff, ((JobAttributeDefinition)(await attributes.GetDefinitionsAsync<JobAttributeDefinition>()).Value!.Single()).Audience);

        Assert.True((await platformFields.StepFieldAsync(CustomFieldTarget.Job, room, CustomFieldSteps.Deactivate, cancellationToken: Ct)).IsSuccess);
        Assert.DoesNotContain((await attributes.QueryDefinitionsAsync(new ListQuery())).Value!.Rows, field => field.Id == room);
        Assert.Empty((await attributes.GetDefinitionsAsync<JobAttributeDefinition>()).Value!);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.True(await system.JobAttributes.AnyAsync(value => value.EntityId == job && value.EntityAttributeDefinitionId == room, Ct));
        }

        Assert.True((await platformFields.StepFieldAsync(CustomFieldTarget.Job, room, CustomFieldSteps.Reactivate, cancellationToken: Ct)).IsSuccess);
        Assert.Contains((await attributes.GetDefinitionsAsync<JobAttributeDefinition>()).Value!, field => field.Id == room);
    }

    private async Task<Guid> FieldAsync(AssignmentScenario scenario, string name, JobFieldAudience audience, bool required = false)
    {
        var onboarded = scenario.StaffMembers.Onboarded;
        await using var console = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime());
        var created = await console.Get<CustomFieldService>().CreateFieldAsync(CustomFieldTarget.Job, new JobAttributeDefinition { State = CustomFieldState.Active, Name = name, ValueType = AttributeValueType.Text, Audience = audience, IsRequired = required }, onboarded.TenantId);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task SetValuesAsync(AssignmentScenario scenario, Guid job, string step, params (Guid Field, string Value)[] values)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        foreach (var (field, value) in values)
        {
            if (updater.Entity.Attributes.FirstOrDefault(row => row.EntityAttributeDefinitionId == field) is { } held)
            {
                held.StringValue = value;
            }
            else
            {
                updater.Entity.Attributes.Add(new JobAttribute { EntityAttributeDefinitionId = field, StringValue = value });
            }
        }

        var saved = await jobs.UpdateJobAndTransitionAsync(updater, step, null, Ct);
        Assert.True(saved.IsSuccess, saved.Error);
    }

    private async Task<IReadOnlyList<Staffora.Core.Models.AssignmentJobField>> FieldsAsync(AssignmentScenario scenario, Guid reader, Guid assignment)
    {
        await using var host = scenario.HostOf(this.database, reader);
        var options = await host.Get<SlotAssignmentService>().GetSlotAssignmentEditorOptionsAsync(assignment, Ct);
        Assert.True(options.IsSuccess, options.Error);
        return options.Value!.JobFields;
    }

    private async Task<string?> ValueAsync(Guid job, Guid field)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.JobAttributes.AsNoTracking().Where(value => value.EntityId == job && value.EntityAttributeDefinitionId == field).Select(value => value.StringValue).SingleOrDefaultAsync(Ct);
    }

    private async Task<Job> StoredAsync(Guid job)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Jobs.AsNoTracking().SingleAsync(row => row.Id == job, Ct);
    }

    private static string Json(object? read) =>
        JsonSerializer.Serialize(read, new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles, MaxDepth = 256 });

    private static Guid Made(Staffora.Core.Models.Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
