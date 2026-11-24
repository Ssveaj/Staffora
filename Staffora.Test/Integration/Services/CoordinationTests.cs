using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class CoordinationTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);

    private readonly RealDatabase database;

    public CoordinationTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_slot_stands_where_its_assignments_and_its_holders_availability_put_it_and_the_Priorities_dock_lists_them_most_urgent_first()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Workspace Kinds", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant", "Tutor", "Mentor", "Reader"]);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[1], bo, Ct));
        await using (var staff = scenario.HostOf(this.database, bo))
        {
            Made(await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), bo, AbsenceKind.SickLeave, new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 8), Ct));
        }

        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[2], cy, Ct));
        var yes = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[3], dee, Ct));
        Made(await scenario.StepAsync(this.database, dee, yes, SlotAssignmentWorkflowAttribute.AcceptStep, Ct));

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var snapshot = Read(await host.Get<CoordinationService>().ReadAsync(Ct));
        var bySlot = snapshot.All.ToDictionary(row => row.Slot);
        Assert.Equal(
            [("Lead", PriorityKind.Covered), ("Assistant", PriorityKind.AtRisk), ("Tutor", PriorityKind.Unanswered), ("Mentor", PriorityKind.WaitingForPick), ("Reader", PriorityKind.Uncovered)],
            slots.Select(id => snapshot.All.Single(row => row.SlotId == id)).Select(row => (row.Slot, row.Kind)));
        Assert.Equal(["Bo Example, the assigned staff member, is not free for 1 of its 8 remaining occurrences.", "On sick leave (reported): 1 occurrence, 2027-03-08."], bySlot["Assistant"].Reasons);
        Assert.Equal(("Ada Example", "1 asked", "1 asked, 1 said yes", "Nobody asked"), (bySlot["Lead"].StaffMembersLabel, bySlot["Tutor"].StaffMembersLabel, bySlot["Mentor"].StaffMembersLabel, bySlot["Reader"].StaffMembersLabel));
        Assert.All(snapshot.All, row => Assert.Equal((job, "Brightpath Learning", "Starts in 2 months"), (row.JobId, row.Customer, row.StartsLabel)));

        var board = Read(await host.Get<CoordinationService>().ReadPrioritiesAsync(snapshot, new PriorityQuery(), Ct));
        Assert.Equal(
            [(PriorityItemKind.AtRisk, "Algebra, 1 at risk"), (PriorityItemKind.ToRequestOrAssign, "Algebra, 1 of 5 open"), (PriorityItemKind.UnansweredRequests, "Algebra, 1 asked, no answer"), (PriorityItemKind.WaitingForPick, "Algebra, 1 said yes"), (PriorityItemKind.AbsenceImpact, "Bo Example")],
            board.Items.Select(item => (item.Kind, item.Kind == PriorityItemKind.AbsenceImpact ? item.Title : item.Text)));
        Assert.All(board.Items.Where(item => item.Kind != PriorityItemKind.AbsenceImpact), item => Assert.Equal((PriorityTarget.Job, job), (item.Target, item.TargetId)));
        Assert.Equal(PriorityTarget.AbsenceImpact, board.Items.Single(item => item.Kind == PriorityItemKind.AbsenceImpact).Target);
        Assert.Equal((5, 4, 1), (board.Total, board.CountOf(PriorityTab.Slots), board.CountOf(PriorityTab.Absence)));
        Assert.Equal(["Algebra, 1 of 5 open"], Read(await host.Get<CoordinationService>().ReadPrioritiesAsync(snapshot, new PriorityQuery(Search: "read"), Ct)).Items.Select(item => item.Text));
        Assert.Empty(snapshot.Behind);
    }

    [Fact]
    public async Task Only_a_coordinator_of_the_tenant_reads_it_another_tenants_jobs_never_show_and_one_read_is_a_fixed_number_of_queries()
    {
        var first = await AssignmentScenario.CreateAsync(this.database, "Workspace First", Ct);
        var second = await AssignmentScenario.CreateAsync(this.database, "Workspace Second", Ct);
        var ada = await first.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await first.StartedJobAsync(this.database, "First algebra", PeriodStart, PeriodEnd, Ct);
        Made(await first.AssignAsync(this.database, first.Coordinator, slots[0], ada, Ct));
        await second.StartedJobAsync(this.database, "Second physics", PeriodStart, PeriodEnd, Ct);

        await using (var staff = first.HostOf(this.database, ada))
        {
            Assert.Equal(CoordinationService.NoView, (await staff.Get<CoordinationService>().ReadAsync(Ct)).Error);
        }

        await using (var console = ServiceHost.Create(this.database, first.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), AssignmentScenario.UseClock))
        {
            Assert.False((await console.Get<CoordinationService>().ReadAsync(Ct)).IsSuccess);
        }

        await using var host = first.HostOf(this.database, first.Coordinator);
        var coordination = host.Get<CoordinationService>();
        Assert.Equal(["First algebra"], Read(await coordination.ReadAsync(Ct)).All.Select(row => row.Job).Distinct());

        host.ClearCommandLog();
        Read(await coordination.ReadAsync(Ct));
        var few = host.CommandLog.Count;
        foreach (var name in new[] { "Biology", "Chemistry", "Drama" })
        {
            var staff = await first.AddStaffAsync(this.database, $"{name[..3]}staff", Ct);
            var (_, more) = await first.StartedJobAsync(this.database, name, PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
            Made(await first.AssignAsync(this.database, first.Coordinator, more[0], staff, Ct));
        }

        host.ClearCommandLog();
        Assert.Equal(7, Read(await coordination.ReadAsync(Ct)).All.Count);
        Assert.Equal(few, host.CommandLog.Count);
    }

    [Fact]
    public async Task A_job_still_coordinated_after_its_period_started_and_one_the_job_clock_could_not_move_are_listed_behind_with_why()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Workspace Behind", Ct);
        var (late, _) = await scenario.StartedJobAsync(this.database, "Late algebra", PeriodStart, PeriodEnd, Ct);
        var (stuck, _) = await scenario.StartedJobAsync(this.database, "Stuck physics", PeriodStart, PeriodEnd, Ct);
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Assert.True((await AssignmentScenario.StepJobAsync(coordinator.Get<JobService>(), stuck, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        }

        var clock = new FakeClock(new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
            services.AddSingleton<IStepPreparation>(new FailsFor(stuck));
        });
        var (_, _, failed, _) = await host.Get<JobClock>().RunForTenantAsync(scenario.TenantId, Ct);
        Assert.Equal(1, failed);
        host.Get<IRuntimeExecutionContext>().ApplySession(new RuntimeSession(scenario.Coordinator, IsSuperAdmin: false, BaseTenantId: scenario.TenantId, IsInPlatformTenant: false, ImpersonatedUserId: null, ImpersonatedTenantId: null));

        var snapshot = Read(await host.Get<CoordinationService>().ReadAsync(Ct));
        var behind = snapshot.Behind.ToDictionary(job => job.JobId);
        Assert.Equal(["Late algebra", "Stuck physics"], behind.Values.Select(job => job.Job).Order());

        var orders = Read(await host.Get<CoordinationService>().ReadPrioritiesAsync(snapshot, new PriorityQuery(PriorityTab.Orders), Ct));
        Assert.Equal(2, orders.CountOf(PriorityTab.Orders));
        Assert.All(orders.Items, item => Assert.Equal((PriorityItemKind.JobsBehind, "Behind", PriorityTarget.Job), (item.Kind, item.Label, item.Target)));
        Assert.Equal(new[] { late, stuck }.Order(), orders.Items.Select(item => item.TargetId).Order());
        Assert.DoesNotContain("Behind", Enum.GetNames<PriorityTab>());
        Assert.Equal("Its period started on 2027-02-01 and it is still being coordinated: confirm it once it is staffed, or cancel it.", behind[late].Reason);
        Assert.StartsWith("Its period started on 2027-02-01, but it is still confirmed: the job clock has not put it in progress yet. It could not move it once because of an error, which the platform's error log keeps. It tries again from 2027-02-10 13:05", behind[stuck].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_committed_step_publishes_its_tenant_job_and_staff_member_after_its_commit()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Commit", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var resource = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);

        var heard = new ConcurrentQueue<(TenantChange Change, SlotAssignmentState? Committed)>();
        using var subscription = bus.Subscribe(scenario.TenantId, change =>
        {
            heard.Enqueue((change, CommittedStateOf(slots[0], resource)));
            return Task.CompletedTask;
        });
        await using (var host = Shared(scenario, scenario.Coordinator, bus))
        {
            Made(await host.Get<SlotAssignmentService>().AssignStaffAsync(slots[0], resource, Ct));
        }

        await bus.DeliveredAsync().WaitAsync(Ct);
        var (change, committed) = Assert.Single(heard);
        Assert.Equal(SlotAssignmentState.Assigned, committed);
        Assert.Equal(scenario.TenantId, change.TenantId);
        Assert.Equal([job], change.JobIds);
        Assert.Equal([resource], change.StaffIds);
    }

    [Fact]
    public async Task A_refused_a_rolled_back_and_a_failed_step_publish_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Rollback", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (algebra, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, physics[0], ada, Ct));
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new ConcurrentQueue<TenantChange>();
        using var subscription = bus.Subscribe(scenario.TenantId, change =>
        {
            heard.Enqueue(change);
            return Task.CompletedTask;
        });

        await using (var host = Shared(scenario, scenario.Coordinator, bus))
        {
            Assert.False((await host.Get<SlotAssignmentService>().AssignStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), Ct)).IsSuccess);
        }

        await using (var host = Shared(scenario, scenario.Coordinator, bus))
        {
            var jobs = host.Get<JobService>();
            var updater = (await jobs.LoadJobUpdaterAsync(algebra, Ct)).Value!;
            updater.Entity.TimeSlots.First().Weekday = Weekday.Tuesday;
            var moved = await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct);
            Assert.False(moved.IsSuccess);
            Assert.Contains("Ada Example is not free", moved.Error, StringComparison.Ordinal);
        }

        await using (var host = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator), services => { AssignmentScenario.UseClock(services); services.AddSingleton(bus); }, [new FailsSavingAnAssignmentsStep()]))
        {
            var (_, other) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct);
            Assert.False((await host.Get<SlotAssignmentService>().AssignStaffAsync(other[0], await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), Ct)).IsSuccess);
        }

        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Empty(heard);
    }

    [Fact]
    public async Task A_change_reaches_only_its_own_tenants_pages_a_staff_member_hears_only_their_own_and_the_console_hears_nothing()
    {
        var first = await AssignmentScenario.CreateAsync(this.database, "Channel First", Ct);
        var second = await AssignmentScenario.CreateAsync(this.database, "Channel Second", Ct);
        var (ada, bo) = (await first.AddStaffAsync(this.database, "Ada", Ct), await first.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await first.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var counts = new ConcurrentDictionary<string, int>();
        Func<Task> Count(string page) => () =>
        {
            counts.AddOrUpdate(page, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        };

        var impersonating = first.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(first.Coordinator, first.TenantId);
        await using var firstCoordinator = Shared(first, first.Coordinator, bus);
        await using var secondCoordinator = Shared(second, second.Coordinator, bus);
        await using var adaHost = Shared(first, ada, bus);
        await using var console = ServiceHost.Create(this.database, first.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), services => services.AddSingleton(bus));
        await using var impersonated = ServiceHost.Create(this.database, impersonating, services => services.AddSingleton(bus));
        var subscriptions = new List<IDisposable?>
        {
            await firstCoordinator.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.Coordination, Count("first coordinator"), Ct),
            await secondCoordinator.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.Coordination, Count("second coordinator"), Ct),
            await adaHost.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.OwnWork, Count("ada's own work"), Ct),
            await impersonated.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.Coordination, Count("impersonating the first coordinator"), Ct),
        };
        Assert.All(subscriptions, subscription => Assert.NotNull(subscription));

        Assert.Null(await adaHost.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.Coordination, Count("ada coordinating"), Ct));
        Assert.Null(await console.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.Coordination, Count("console"), Ct));
        Assert.Null(await console.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.OwnWork, Count("console"), Ct));
        Assert.Null(await firstCoordinator.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.OwnWork, Count("coordinator's own work"), Ct));

        await using (var acting = Shared(first, first.Coordinator, bus))
        {
            Made(await acting.Get<SlotAssignmentService>().AssignStaffAsync(slots[1], await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), Ct));
        }

        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Equal((1, 1, 0, 0), (counts.GetValueOrDefault("first coordinator"), counts.GetValueOrDefault("impersonating the first coordinator"), counts.GetValueOrDefault("second coordinator"), counts.GetValueOrDefault("ada's own work")));

        await using (var acting = Shared(first, first.Coordinator, bus))
        {
            Made(await acting.Get<SlotAssignmentService>().AssignStaffAsync(slots[0], await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), Ct));
        }

        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Equal((2, 2, 0, 1), (counts.GetValueOrDefault("first coordinator"), counts.GetValueOrDefault("impersonating the first coordinator"), counts.GetValueOrDefault("second coordinator"), counts.GetValueOrDefault("ada's own work")));
        Assert.DoesNotContain(counts.Keys, page => page is "console" or "ada coordinating" or "coordinator's own work");

        var (_, secondSlots) = await second.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct);
        var cy = await second.AddStaffAsync(this.database, "Cy", Ct);
        await using (var acting = Shared(second, second.Coordinator, bus))
        {
            Made(await acting.Get<SlotAssignmentService>().AssignStaffAsync(secondSlots[0], await AssignmentScenario.ResourceOfAsync(this.database, cy, Ct), Ct));
        }

        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Equal((2, 2, 1, 1), (counts.GetValueOrDefault("first coordinator"), counts.GetValueOrDefault("impersonating the first coordinator"), counts.GetValueOrDefault("second coordinator"), counts.GetValueOrDefault("ada's own work")));

        foreach (var subscription in subscriptions)
        {
            subscription!.Dispose();
        }

        Assert.Equal(0, bus.SubscriptionCount);
    }

    [Fact]
    public async Task The_job_clock_publishes_each_step_after_its_commit()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Clock", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, clockSlots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, clockSlots[0], ada, Ct));
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Assert.True((await AssignmentScenario.StepJobAsync(coordinator.Get<JobService>(), job, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        }

        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new ConcurrentQueue<(TenantChange Change, JobState? Committed)>();
        using var subscription = bus.Subscribe(scenario.TenantId, change =>
        {
            heard.Enqueue((change, CommittedJobStateOf(job)));
            return Task.CompletedTask;
        });
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2027, 2, 1, 12, 0, 0, TimeSpan.Zero)));
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
            services.AddSingleton(bus);
        });
        Assert.Equal(1, (await host.Get<JobClock>().RunForTenantAsync(scenario.TenantId, Ct)).Started);
        await bus.DeliveredAsync().WaitAsync(Ct);
        var (change, committed) = Assert.Single(heard);
        Assert.Equal((scenario.TenantId, JobState.InProgress), (change.TenantId, committed));
        Assert.Contains(job, change.JobIds);

        Assert.Equal([await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct)], change.StaffIds);
    }

    [Fact]
    public async Task A_coordinators_edit_of_a_job_reaches_the_own_pages_of_its_asked_and_assigned_staff_members_and_of_nobody_else()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Job Edit", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        var (algebra, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant", "Tutor"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[1], dee, Ct));
        var declined = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[2], bo, Ct));
        Made(await scenario.StepAsync(this.database, bo, declined, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, comment: "Not this period."));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, physics[0], cy, Ct));

        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var counts = new ConcurrentDictionary<string, int>();
        Func<Task> Count(string page) => () =>
        {
            counts.AddOrUpdate(page, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        };

        var heard = new ConcurrentQueue<TenantChange>();
        using var all = bus.Subscribe(scenario.TenantId, change =>
        {
            heard.Enqueue(change);
            return Task.CompletedTask;
        });
        var hosts = new[] { (ada, "ada"), (bo, "bo"), (cy, "cy"), (dee, "dee") }.Select(staff => (Host: Shared(scenario, staff.Item1, bus), Page: staff.Item2)).ToList();
        try
        {
            foreach (var (host, page) in hosts)
            {
                Assert.NotNull(await host.Get<TenantChangeFeed>().SubscribeAsync(ChangeInterest.OwnWork, Count(page), Ct));
            }

            await using (var coordinator = Shared(scenario, scenario.Coordinator, bus))
            {
                var jobs = coordinator.Get<JobService>();
                var updater = (await jobs.LoadJobUpdaterAsync(algebra, Ct)).Value!;
                updater.Entity.TimeSlots.First().Weekday = Weekday.Wednesday;
                Made(await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct));
                updater = (await jobs.LoadJobUpdaterAsync(algebra, Ct)).Value!;
                updater.Entity.Name = "Algebra for adults";
                Made(await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct));
            }

            await bus.DeliveredAsync().WaitAsync(Ct);
            Assert.Equal((2, 2, 0, 0), (counts.GetValueOrDefault("ada"), counts.GetValueOrDefault("dee"), counts.GetValueOrDefault("bo"), counts.GetValueOrDefault("cy")));
            var (adaRecord, deeRecord) = (await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), await AssignmentScenario.ResourceOfAsync(this.database, dee, Ct));
            Assert.Equal(2, heard.Count);
            Assert.All(heard, change => Assert.Equal(new HashSet<Guid> { adaRecord, deeRecord }, change.StaffIds));
            Assert.All(heard, change => Assert.Equal([algebra], change.JobIds));
        }
        finally
        {
            foreach (var (host, _) in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Giving_and_taking_away_a_staff_members_role_publishes_their_record_and_a_holder_taken_out_of_service_puts_the_slot_at_risk()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Roles", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        var adaRecord = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new ConcurrentQueue<TenantChange>();
        using var subscription = bus.Subscribe(scenario.TenantId, change =>
        {
            heard.Enqueue(change);
            return Task.CompletedTask;
        });

        var pat = await scenario.StaffMembers.AddPersonAsync(this.database, "Pat", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime(), services =>
        {
            AssignmentScenario.UseClock(services);
            services.AddSingleton(bus);
        });
        var tenants = owner.Get<RoleHoldingService>();
        var given = await tenants.AssignUserToRoleAsync(pat, scenario.StaffMembers.TeamId, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.TenantId);
        Assert.True(given.IsSuccess, given.Error);
        await bus.DeliveredAsync().WaitAsync(Ct);
        var patRecord = await AssignmentScenario.ResourceOfAsync(this.database, pat, Ct);
        Assert.Single(heard, change => change.StaffIds.Contains(patRecord));

        heard.Clear();
        Guid holding;
        await using (var system = this.database.CreateSystemContext())
        {
            holding = await system.OrganizationUnitUserRoles.AsNoTracking().Where(row => row.UserId == ada && row.TenantRoleId == scenario.StaffMembers.Onboarded.Roles.Staff).Select(row => row.Id).SingleAsync(Ct);
        }

        Made(await tenants.RemoveUserFromRoleAsync(holding));
        await bus.DeliveredAsync().WaitAsync(Ct);
        var change = Assert.Single(heard);
        Assert.Equal(scenario.TenantId, change.TenantId);
        Assert.Contains(adaRecord, change.StaffIds);
        await using var coordinator = scenario.HostOf(this.database, scenario.Coordinator);
        Assert.Equal(PriorityKind.AtRisk, Read(await coordinator.Get<CoordinationService>().ReadAsync(Ct)).All.Single(row => row.SlotId == slots[0]).Kind);
    }

    [Fact]
    public async Task A_staff_member_reads_no_coordination_even_in_a_tenant_where_nobody_holds_a_slot_so_no_availability_is_read()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Workspace No Holder", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        await using var staff = scenario.HostOf(this.database, ada);
        var read = await staff.Get<CoordinationService>().ReadAsync(Ct);
        Assert.False(read.IsSuccess);
        Assert.Equal(CoordinationService.NoView, read.Error);
        Assert.Null(read.Value);
    }

    private ServiceHost Shared(AssignmentScenario scenario, Guid userId, TenantChangeBus bus) =>
        ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(userId), services =>
        {
            AssignmentScenario.UseClock(services);
            services.AddSingleton(bus);
        });

    private SlotAssignmentState? CommittedStateOf(Guid slot, Guid resource) =>
        ReadAtOnce(context => context.SlotAssignments.AsNoTracking().Where(row => row.JobSlotId == slot && row.ResourceId == resource).Select(row => (SlotAssignmentState?)row.State).SingleOrDefault());

    private JobState? CommittedJobStateOf(Guid job) =>
        ReadAtOnce(context => context.Jobs.AsNoTracking().Where(row => row.Id == job).Select(row => (JobState?)row.State).SingleOrDefault());

    private T? ReadAtOnce<T>(Func<ApplicationDbContext, T?> read)
    {
        using var reader = this.database.CreateSystemContext();
        reader.Database.OpenConnection();
        reader.Database.ExecuteSqlRaw("SET LOCK_TIMEOUT 0");
        try
        {
            return read(reader);
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return default;
        }
    }

    private static PriorityBoard Read(Result<PriorityBoard> read)
    {
        Assert.True(read.IsSuccess, read.Error);
        return read.Value!;
    }

    private static CoordinationSnapshot Read(Result<CoordinationSnapshot> read)
    {
        Assert.True(read.IsSuccess, read.Error);
        return read.Value!;
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);

    private sealed class FailsFor : IStepPreparation
    {
        private readonly Guid jobId;

        public FailsFor(Guid jobId)
        {
            this.jobId = jobId;
        }

        public Type EntityType => typeof(Job);

        public Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken) =>
            entityId == this.jobId ? throw new InvalidOperationException("The test broke this job's step.") : Task.CompletedTask;
    }

    private sealed class FailsSavingAnAssignmentsStep : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            eventData.Context?.ChangeTracker.Entries<SlotAssignment>().Any(entry => entry.State == EntityState.Modified) == true
                ? throw new InvalidOperationException("The test failed this save.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
