using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class SlotGroupTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public SlotGroupTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_add_with_a_count_of_three_makes_three_slots_alike_and_three_staff_assigned_to_the_group_take_one_slot_each()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Group Add Staffing", Ct);
        var staff = new List<Guid>();
        foreach (var name in new[] { "Ada", "Bo", "Cy", "Dee" })
        {
            staff.Add(await scenario.AddStaffAsync(this.database, name, Ct));
        }

        var (job, _) = await scenario.StartedJobAsync(this.database, "Office cleaning", PeriodStart, PeriodEnd, Ct, slots: ["Supervisor"]);

        Guid first;
        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            first = Made(await host.Get<JobSlotService>().CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = 3 }, JobSlotWorkflowAttribute.AddStep, Ct));
        }

        var cleaners = await SlotsAsync(job, "Cleaner");
        Assert.Equal(3, cleaners.Count);
        Assert.All(cleaners, slot => Assert.Equal(JobSlotState.Active, slot.State));
        Assert.Contains(first, cleaners.Select(slot => slot.Id));
        await using (var context = this.database.CreateSystemContext())
        {
            var ids = cleaners.Select(slot => slot.Id).ToList();
            var added = await context.StateHistoryEntries.AsNoTracking().Where(entry => ids.Contains(entry.EntityId)).ToListAsync(Ct);
            Assert.Equal(3, added.Count);
            Assert.All(added, entry => Assert.Equal((JobSlotWorkflowAttribute.AddStep, (int)JobSlotState.Create, (int)JobSlotState.Active), (entry.TransitionKey, entry.FromState, entry.ToState)));
            Assert.Single(added.Select(entry => entry.CorrelationId).Distinct());
        }

        var group = await GroupRowAsync(scenario, job, "Cleaner");
        Assert.Equal((first, 3, "0 of 3"), (group.Id, group.SlotCount, group.HeldLabel));

        var assignments = new List<Guid>();
        foreach (var member in staff.Take(3))
        {
            assignments.Add(Made(await scenario.AssignAsync(this.database, scenario.Coordinator, first, member, Ct)));
        }

        Assert.Equal(SlotAssignmentWorkflowAttribute.GroupOpenReason, (await scenario.AssignAsync(this.database, scenario.Coordinator, first, staff[3], Ct)).Error);
        Assert.Equal(SlotAssignmentWorkflowAttribute.GroupOpenReason, (await scenario.RequestAsync(this.database, scenario.Coordinator, first, staff[3], Ct)).Error);
        var stored = new List<SlotAssignment>();
        foreach (var assignment in assignments)
        {
            stored.Add((await AssignmentScenario.StoredAsync(this.database, assignment, Ct))!);
        }

        Assert.All(stored, assignment => Assert.Equal(SlotAssignmentState.Assigned, assignment.State));
        Assert.Equal(cleaners.Select(slot => slot.Id).Order(), stored.Select(assignment => assignment.JobSlotId).Order());
        Assert.Equal(first, stored[0].JobSlotId);

        var full = await GroupRowAsync(scenario, job, "Cleaner");
        Assert.Equal(("3 of 3", 3), (full.CoverageLabel, full.Holders.Count));
    }

    [Fact]
    public async Task An_add_out_of_range_or_with_a_refused_row_writes_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Group Refused Staffing", Ct);
        var (job, _) = await scenario.StartedJobAsync(this.database, "Office cleaning", PeriodStart, PeriodEnd, Ct, slots: ["Supervisor"]);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var slots = host.Get<JobSlotService>();

        Assert.Equal(JobSlotService.CountOutOfRange, (await slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = 0 }, JobSlotWorkflowAttribute.AddStep, Ct)).Error);
        Assert.Equal(JobSlotService.CountOutOfRange, (await slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = 21 }, JobSlotWorkflowAttribute.AddStep, Ct)).Error);
        Assert.Equal(JobSlotService.QualificationNotActive, (await slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = 2, Qualifications = [new JobSlotQualification { QualificationId = Guid.NewGuid() }] }, JobSlotWorkflowAttribute.AddStep, Ct)).Error);
        Assert.Equal(JobSlotService.CreateBySave, (await slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner" }, JobSlotWorkflowAttribute.SaveStep, Ct)).Error);

        var good = new JobSlot { TenantId = scenario.TenantId, State = JobSlotState.Create, JobId = job, Name = "Cleaner" };
        var refused = new JobSlot { TenantId = scenario.TenantId, State = JobSlotState.Create, JobId = job, Name = "Cleaner", Qualifications = [new JobSlotQualification { TenantId = scenario.TenantId, QualificationId = Guid.NewGuid() }] };
        var batch = await host.Get<WorkflowService>().CreateManyAndTransitionAsync<JobSlotState, JobSlot>([good, refused], JobSlotWorkflowAttribute.AddStep, JobSlotWorkflowQueries.WithWorkflowGraph, Ct);
        Assert.False(batch.IsSuccess);
        Assert.Empty(await SlotsAsync(job, "Cleaner"));
        await using var context = this.database.CreateSystemContext();
        Assert.Equal(1, await context.StateHistoryEntries.AsNoTracking().CountAsync(entry => entry.EntityType == nameof(JobSlot) && context.JobSlots.Any(slot => slot.Id == entry.EntityId && slot.JobId == job), Ct));
    }

    [Fact]
    public async Task A_copy_of_a_slot_joins_its_group_and_a_copy_of_another_jobs_slot_is_not_found()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Group Copy Staffing", Ct);
        var (job, _) = await scenario.StartedJobAsync(this.database, "Office cleaning", PeriodStart, PeriodEnd, Ct, slots: ["Supervisor"]);
        var (other, otherSlots) = await scenario.StartedJobAsync(this.database, "Warehouse cleaning", PeriodStart, PeriodEnd, Ct, slots: ["Cleaner"]);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var slots = host.Get<JobSlotService>();
        var qualification = Made(await host.Get<Staffora.Services.Resources.QualificationService>().CreateQualificationAsync(new Qualification { Name = "Floor care" }, QualificationWorkflowAttribute.CreateStep, Ct));
        var first = Made(await slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Description = "Floors and desks", Count = 3, Qualifications = [new JobSlotQualification { QualificationId = qualification }] }, JobSlotWorkflowAttribute.AddStep, Ct));

        var copy = (await slots.NewJobSlotAsync(job, first, Ct)).Value!;
        Assert.Equal(("Cleaner", "Floors and desks", JobSlotState.Create, 1), (copy.Name, copy.Description, copy.State, copy.Count));
        Assert.Equal([qualification], copy.Qualifications.Select(required => required.QualificationId));
        Made(await slots.CreateJobSlotAsync(copy, JobSlotWorkflowAttribute.AddStep, Ct));

        var group = await GroupRowAsync(scenario, job, "Cleaner");
        Assert.Equal((first, 4), (group.Id, group.SlotCount));

        Assert.Equal(JobSlotService.NotFound, (await slots.NewJobSlotAsync(job, otherSlots[0], Ct)).Error);
        Assert.Single(await SlotsAsync(other, "Cleaner"));
    }

    [Fact]
    public async Task A_group_of_one_still_closes_its_other_live_requests_correlated_with_the_assignment_and_a_larger_group_keeps_them_until_full()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Group Settle Staffing", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var coordinator = scenario.Coordinator;

        var adaRequest = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct));
        var boRequest = Made(await scenario.RequestAsync(this.database, coordinator, slots[0], bo, Ct));
        Assert.True((await scenario.StepAsync(this.database, bo, boRequest, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        var cyAssigned = Made(await scenario.AssignAsync(this.database, coordinator, slots[0], cy, Ct));
        Assert.Equal([SlotAssignmentState.NotSelected, SlotAssignmentState.NotSelected, SlotAssignmentState.Assigned], await AssignmentScenario.StatesAsync(this.database, Ct, adaRequest, boRequest, cyAssigned));
        Assert.Equal(SlotAssignmentWorkflowAttribute.CloseRequestStep, (await AssignmentScenario.HistoryAsync(this.database, adaRequest, Ct))[^1]);
        Assert.Equal(SlotAssignmentWorkflowAttribute.NotSelectedStep, (await AssignmentScenario.HistoryAsync(this.database, boRequest, Ct))[^1]);
        await using (var context = this.database.CreateSystemContext())
        {
            var assign = await context.StateHistoryEntries.AsNoTracking().SingleAsync(entry => entry.EntityId == cyAssigned, Ct);
            var closed = await context.StateHistoryEntries.AsNoTracking().Where(entry => (entry.EntityId == adaRequest || entry.EntityId == boRequest) && (entry.TransitionKey == SlotAssignmentWorkflowAttribute.CloseRequestStep || entry.TransitionKey == SlotAssignmentWorkflowAttribute.NotSelectedStep)).ToListAsync(Ct);
            Assert.Equal(2, closed.Count);
            Assert.All(closed, entry => Assert.Equal(assign.CorrelationId, entry.CorrelationId));
        }

        Assert.Equal(("Another staff member was picked for Algebra", $"?dlg=assignment-{adaRequest}"), Subject((await scenario.NoticesOfAsync(this.database, ada, Ct))[0]));
        Assert.Equal(("Another staff member was picked for Algebra", $"?dlg=assignment-{boRequest}"), Subject((await scenario.NoticesOfAsync(this.database, bo, Ct))[0]));

        Guid pair;
        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            pair = Made(await host.Get<JobSlotService>().CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Assistant", Count = 2 }, JobSlotWorkflowAttribute.AddStep, Ct));
        }

        var adaAgain = Made(await scenario.RequestAsync(this.database, coordinator, pair, ada, Ct));
        Made(await scenario.AssignAsync(this.database, coordinator, pair, bo, Ct));
        Assert.Equal([SlotAssignmentState.Requested], await AssignmentScenario.StatesAsync(this.database, Ct, adaAgain));
        Assert.Contains(SlotAssignmentWorkflowAttribute.OneLiveReason, (await scenario.AssignAsync(this.database, coordinator, pair, bo, Ct)).Error, StringComparison.Ordinal);
        Made(await scenario.AssignAsync(this.database, coordinator, pair, dee, Ct));
        Assert.Equal([SlotAssignmentState.NotSelected], await AssignmentScenario.StatesAsync(this.database, Ct, adaAgain));
    }

    private async Task<List<JobSlot>> SlotsAsync(Guid job, string name)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.JobSlots.AsNoTracking().Where(slot => slot.JobId == job && slot.Name == name && slot.State == JobSlotState.Active).ToListAsync(Ct);
    }

    private async Task<JobSlotRow> GroupRowAsync(AssignmentScenario scenario, Guid job, string name)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var page = await host.Get<JobService>().QueryJobSlotsAsync(job, new ListQuery(), Ct);
        Assert.True(page.IsSuccess, page.Error);
        return Assert.Single(page.Value!.Rows, row => row.Name == name);
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static (string Subject, string? Link) Subject((string Subject, string Text, string? Link) notice) => (notice.Subject, notice.Link);
}
