using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class AssignmentConcurrencyTests : IClassFixture<RealDatabase>
{
    private const int Rounds = 3;

    private readonly RealDatabase database;

    public AssignmentConcurrencyTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_concurrent_assigns_to_one_slot_leave_exactly_one_holder_and_refuse_the_other_with_its_reason()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Race Staffing", Ct);
        var second = await SecondCoordinatorAsync(scenario);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Raced job", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, [.. Enumerable.Range(1, Rounds).Select(round => $"Slot {round}")]);

        foreach (var (slot, round) in slots.Select((slot, index) => (slot, index)))
        {
            var (ada, bo) = (await scenario.AddStaffAsync(this.database, $"Ada{round}", Ct), await scenario.AddStaffAsync(this.database, $"Bo{round}", Ct));
            var (adaRecord, boRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct));
            await using var first = scenario.HostOf(this.database, scenario.Coordinator);
            await using var other = scenario.HostOf(this.database, second);
            var (left, right) = (first.Get<SlotAssignmentService>(), other.Get<SlotAssignmentService>());
            using var start = new SemaphoreSlim(0, 2);
            var racers = new[]
            {
                Task.Run(async () => { await start.WaitAsync(Ct); return await left.AssignStaffAsync(slot, adaRecord, Ct); }, Ct),
                Task.Run(async () => { await start.WaitAsync(Ct); return await right.AssignStaffAsync(slot, boRecord, Ct); }, Ct),
            };
            start.Release(2);
            var results = await Task.WhenAll(racers);

            Assert.Single(results, result => result.IsSuccess);
            Assert.Equal(SlotAssignmentWorkflowAttribute.OneHolderReason, Assert.Single(results, result => !result.IsSuccess).Error);
            Assert.Equal(1, await AssignedOnAsync(slot));
            Assert.Equal(1, await StoredOnAsync(slot));
        }
    }

    [Fact]
    public async Task Two_concurrent_picks_of_two_yeses_leave_exactly_one_holder_and_the_other_yes_closed_or_refused()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Pick Race Staffing", Ct);
        var second = await SecondCoordinatorAsync(scenario);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Picked job", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, [.. Enumerable.Range(1, Rounds).Select(round => $"Slot {round}")]);

        foreach (var (slot, round) in slots.Select((slot, index) => (slot, index)))
        {
            var (ada, bo) = (await scenario.AddStaffAsync(this.database, $"Ada{round}", Ct), await scenario.AddStaffAsync(this.database, $"Bo{round}", Ct));
            var adaAssignment = (await scenario.RequestAsync(this.database, scenario.Coordinator, slot, ada, Ct)).Value;
            var boAssignment = (await scenario.RequestAsync(this.database, scenario.Coordinator, slot, bo, Ct)).Value;
            Assert.True((await scenario.StepAsync(this.database, ada, adaAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
            Assert.True((await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);

            await using var first = scenario.HostOf(this.database, scenario.Coordinator);
            await using var other = scenario.HostOf(this.database, second);
            var (left, right) = (first.Get<SlotAssignmentService>(), other.Get<SlotAssignmentService>());
            var (leftUpdater, rightUpdater) = ((await left.LoadSlotAssignmentUpdaterAsync(adaAssignment, Ct)).Value!, (await right.LoadSlotAssignmentUpdaterAsync(boAssignment, Ct)).Value!);
            using var start = new SemaphoreSlim(0, 2);
            var racers = new[]
            {
                Task.Run(async () => { await start.WaitAsync(Ct); return await left.UpdateSlotAssignmentAndTransitionAsync(leftUpdater, SlotAssignmentWorkflowAttribute.PickStep, null, Ct); }, Ct),
                Task.Run(async () => { await start.WaitAsync(Ct); return await right.UpdateSlotAssignmentAndTransitionAsync(rightUpdater, SlotAssignmentWorkflowAttribute.PickStep, null, Ct); }, Ct),
            };
            start.Release(2);
            var results = await Task.WhenAll(racers);

            Assert.Single(results, result => result.IsSuccess);
            var refused = Assert.Single(results, result => !result.IsSuccess);
            Assert.DoesNotContain("could not be completed", refused.Error, StringComparison.Ordinal);
            Assert.Equal(1, await AssignedOnAsync(slot));
            var states = await AssignmentScenario.StatesAsync(this.database, Ct, adaAssignment, boAssignment);
            Assert.Equal([SlotAssignmentState.Assigned, SlotAssignmentState.NotSelected], states.Order());
        }
    }

    [Fact]
    public async Task An_absence_approved_while_an_assignment_of_its_staff_member_is_judged_waits_for_it_and_one_approved_first_refuses_it()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Absence Race", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Raced absence", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, ["Lead", "Assistant"]);
        var leave = await LeaveAsync(scenario, ada, new DateOnly(2027, 3, 8));

        var approvedDuringTheStep = false;
        Task<Result>? approval = null;
        var during = new DuringCreate(async () =>
        {
            approval = Task.Run(async () =>
            {
                await using var approving = scenario.HostOf(this.database, scenario.Absence.Approver);
                return await AbsenceScenario.StepAsync(approving.Get<ResourceAbsenceService>(), leave, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct);
            }, Ct);

            var until = DateTime.UtcNow.AddSeconds(20);
            while (!approval.IsCompleted && await ApplicationLockWaitersAsync() == 0 && DateTime.UtcNow < until)
            {
                await Task.Delay(25, Ct);
            }

            approvedDuringTheStep = approval.IsCompleted;
        });

        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator), services =>
        {
            AssignmentScenario.UseClock(services);
            services.AddScoped<IStepPreparation>(_ => during);
        }))
        {
            var assigned = await host.Get<SlotAssignmentService>().AssignStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), Ct);
            Assert.True(assigned.IsSuccess, assigned.Error);
        }

        Assert.False(approvedDuringTheStep, "The approval ran while the assignment was judged on the absences as they were before it.");
        var approved = await approval!;
        Assert.True(approved.IsSuccess, approved.Error);
        Assert.Equal(ResourceAbsenceState.Approved, (await AbsenceScenario.StoredAsync(this.database, leave, Ct))!.State);

        var refused = await scenario.AssignAsync(this.database, scenario.Coordinator, slots[1], ada, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains("On planned leave: 1 occurrence, 2027-03-08", refused.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_approvals_and_assignments_never_leave_an_assignment_committed_after_its_staff_members_absence()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Absence Rounds", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Raced rounds", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, [.. Enumerable.Range(1, Rounds).Select(round => $"Slot {round}")]);
        foreach (var (slot, round) in slots.Select((slot, index) => (slot, index)))
        {
            var staff = await scenario.AddStaffAsync(this.database, $"Racer{round}", Ct);
            var leave = await LeaveAsync(scenario, staff, new DateOnly(2027, 3, 8));
            var record = await AssignmentScenario.ResourceOfAsync(this.database, staff, Ct);
            await using var coordinating = scenario.HostOf(this.database, scenario.Coordinator);
            await using var approving = scenario.HostOf(this.database, scenario.Absence.Approver);
            using var start = new SemaphoreSlim(0, 2);
            var assign = Task.Run(async () => { await start.WaitAsync(Ct); return (Result)await coordinating.Get<SlotAssignmentService>().AssignStaffAsync(slot, record, Ct); }, Ct);
            var approve = Task.Run(async () => { await start.WaitAsync(Ct); return await AbsenceScenario.StepAsync(approving.Get<ResourceAbsenceService>(), leave, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct); }, Ct);
            start.Release(2);
            var (assigned, approved) = (await assign, await approve);

            Assert.True(approved.IsSuccess, approved.Error);
            if (assigned.IsSuccess)
            {
                Assert.True(await CommittedBeforeAsync(slot, leave), $"Round {round}: the assignment committed after the approval of its staff member's absence.");
            }
            else
            {
                Assert.Contains("On planned leave", assigned.Error, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Two_coordinators_assigning_one_staff_member_to_two_jobs_at_the_same_time_book_her_once()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Double Booking Race", Ct);
        var second = await SecondCoordinatorAsync(scenario);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Raced algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, [.. Enumerable.Range(1, Rounds).Select(round => $"Slot {round}")]);
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Raced physics", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), Ct, [.. Enumerable.Range(1, Rounds).Select(round => $"Slot {round}")], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(9, 0), new TimeOnly(11, 0)));
        for (var round = 0; round < Rounds; round++)
        {
            var record = await AssignmentScenario.ResourceOfAsync(this.database, await scenario.AddStaffAsync(this.database, $"Booked{round}", Ct), Ct);
            await using var first = scenario.HostOf(this.database, scenario.Coordinator);
            await using var other = scenario.HostOf(this.database, second);
            var (left, right) = (first.Get<SlotAssignmentService>(), other.Get<SlotAssignmentService>());
            using var start = new SemaphoreSlim(0, 2);
            var (algebraSlot, physicsSlot) = (algebra[round], physics[round]);
            var racers = new[]
            {
                Task.Run(async () => { await start.WaitAsync(Ct); return await left.AssignStaffAsync(algebraSlot, record, Ct); }, Ct),
                Task.Run(async () => { await start.WaitAsync(Ct); return await right.AssignStaffAsync(physicsSlot, record, Ct); }, Ct),
            };
            start.Release(2);
            var results = await Task.WhenAll(racers);

            Assert.Single(results, result => result.IsSuccess);
            Assert.Contains(AvailabilityRules.AssignedReason, Assert.Single(results, result => !result.IsSuccess).Error, StringComparison.Ordinal);
            Assert.Equal(1, await AssignedOnAsync(algebraSlot) + await AssignedOnAsync(physicsSlot));
            Assert.Equal(1, await StoredOnAsync(algebraSlot) + await StoredOnAsync(physicsSlot));
        }
    }

    private async Task<Guid> LeaveAsync(AssignmentScenario scenario, Guid staff, DateOnly day)
    {
        await using var registering = scenario.HostOf(this.database, scenario.Absence.Registrar);
        var registered = await AbsenceScenario.RegisterAsync(registering.Get<ResourceAbsenceService>(), staff, AbsenceKind.PlannedLeave, day, day, Ct);
        Assert.True(registered.IsSuccess, registered.Error);
        return registered.Value;
    }

    private async Task<bool> CommittedBeforeAsync(Guid slot, Guid absence)
    {
        await using var context = this.database.CreateSystemContext();
        var assignment = await context.SlotAssignments.Where(row => row.JobSlotId == slot).Select(row => row.Id).SingleAsync(Ct);
        var assigned = await context.StateHistoryEntries.Where(entry => entry.EntityId == assignment).Select(entry => entry.Created).MinAsync(Ct);
        var approved = await context.StateHistoryEntries.Where(entry => entry.EntityId == absence && entry.TransitionKey == ResourceAbsenceWorkflowAttribute.ApproveStep).Select(entry => entry.Created).SingleAsync(Ct);
        return assigned < approved;
    }

    private async Task<int> ApplicationLockWaitersAsync()
    {
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND resource_database_id = DB_ID()";
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class DuringCreate : IStepPreparation
    {
        private readonly Func<Task> during;

        public DuringCreate(Func<Task> during)
        {
            this.during = during;
        }

        public Type EntityType => typeof(SlotAssignment);

        public Task PrepareAsync(Staffora.Data.ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PrepareNewAsync(Staffora.Data.ApplicationDbContext context, object entity, CancellationToken cancellationToken) => this.during();
    }

    private async Task<Guid> SecondCoordinatorAsync(AssignmentScenario scenario)
    {
        var second = await scenario.StaffMembers.AddPersonAsync(this.database, "Corey", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        var given = await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(second, scenario.StaffMembers.RootId, scenario.StaffMembers.Onboarded.Roles.Coordinator, scenario.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        return second;
    }

    private async Task<int> AssignedOnAsync(Guid slot)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.SlotAssignments.CountAsync(assignment => assignment.JobSlotId == slot && assignment.State == SlotAssignmentState.Assigned, Ct);
    }

    private async Task<int> StoredOnAsync(Guid slot)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.SlotAssignments.CountAsync(assignment => assignment.JobSlotId == slot, Ct);
    }
}
