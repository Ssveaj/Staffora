using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Customers;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class BoardChangeChannelTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);

    private readonly RealDatabase database;

    public BoardChangeChannelTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_qualification_given_or_taken_away_publishes_its_staff_member_after_the_commit_and_a_refused_one_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Qualifications", Ct);
        var other = await AssignmentScenario.CreateAsync(this.database, "Channel Qualifications Other", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var adaRecord = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = new ConcurrentQueue<(TenantChange Change, bool Committed)>();
        Guid physics = Guid.Empty;
        using var mine = bus.Subscribe(scenario.TenantId, change =>
        {
            heard.Enqueue((change, HeldAtOnce(adaRecord, physics)));
            return Task.CompletedTask;
        });
        var theirs = new ConcurrentQueue<TenantChange>();
        using var stranger = bus.Subscribe(other.TenantId, change =>
        {
            theirs.Enqueue(change);
            return Task.CompletedTask;
        });

        await using var keeper = Shared(scenario, scenario.Coordinator, bus);
        var qualifications = keeper.Get<QualificationService>();
        physics = Made(await qualifications.CreateQualificationAsync(new Qualification { Name = "Physics" }, QualificationWorkflowAttribute.CreateStep, Ct));
        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Empty(heard);

        Made(await qualifications.GiveQualificationAsync(ada, physics, Ct));
        await bus.DeliveredAsync().WaitAsync(Ct);
        var (given, committed) = Assert.Single(heard);
        Assert.True(committed);
        Assert.Equal((scenario.TenantId, 0), (given.TenantId, given.JobIds.Count));
        Assert.Equal([adaRecord], given.StaffIds);

        heard.Clear();
        Assert.False((await qualifications.GiveQualificationAsync(ada, physics, Ct)).IsSuccess);
        await using (var staff = Shared(scenario, ada, bus))
        {
            Assert.False((await staff.Get<QualificationService>().TakeQualificationAsync(ada, physics, Ct)).IsSuccess);
        }

        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Empty(heard);

        Made(await qualifications.TakeQualificationAsync(ada, physics, Ct));
        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Equal([adaRecord], Assert.Single(heard).Change.StaffIds);
        Assert.Empty(theirs);
    }

    [Fact]
    public async Task A_users_rename_in_the_dialog_publishes_their_staff_record_and_a_rename_of_someone_who_is_no_staff_member_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Renames", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var adaRecord = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = Listen(bus, scenario.TenantId);

        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime(), services => services.AddSingleton(bus)))
        {
            Made(await owner.Get<UserService>().SaveUserAsync(ada, user => user.LastName = "Lovelace"));
            await bus.DeliveredAsync().WaitAsync(Ct);
            var renamed = Assert.Single(heard);
            Assert.Equal([adaRecord], renamed.StaffIds);
            Assert.Empty(renamed.JobIds);

            heard.Clear();
            Made(await owner.Get<UserService>().SaveUserAsync(scenario.Coordinator, user => user.LastName = "Coordinator"));
            await bus.DeliveredAsync().WaitAsync(Ct);
            Assert.Empty(heard);
        }
    }

    [Fact]
    public async Task A_customers_rename_publishes_its_jobs_and_their_asked_and_assigned_staff_members()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Customer", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = Listen(bus, scenario.TenantId);

        await using (var coordinator = Shared(scenario, scenario.Coordinator, bus))
        {
            var customers = coordinator.Get<CustomerService>();
            var updater = (await customers.LoadCustomerUpdaterAsync(scenario.Customer, Ct)).Value!;
            updater.Entity.Name = "Brightpath Learning Group";
            Made(await customers.UpdateCustomerAndTransitionAsync(updater, CustomerWorkflowAttribute.SaveStep, null, Ct));
        }

        await bus.DeliveredAsync().WaitAsync(Ct);
        var change = Assert.Single(heard);
        Assert.Equal([job], change.JobIds);
        Assert.Equal([await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct)], change.StaffIds);
        Assert.DoesNotContain(await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), change.StaffIds);
    }

    [Fact]
    public async Task A_work_patterns_own_step_publishes_the_staff_members_whose_hours_it_gives_and_a_draft_nobody_has_nothing()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Channel Patterns", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2026, 11, 2), Ct);
        await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", new DateOnly(2026, 11, 2), Ct);
        var adaRecord = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        Guid pattern;
        await using (var system = this.database.CreateSystemContext())
        {
            pattern = await system.ResourcePatternAssignments.AsNoTracking().Where(row => row.ResourceId == adaRecord).Select(row => row.WorkPatternId).SingleAsync(Ct);
        }

        var bus = new TenantChangeBus(NullLogger<TenantChangeBus>.Instance);
        var heard = Listen(bus, scenario.TenantId);
        await using var admin = Shared(scenario, scenario.Absence.Schedule.Admin, bus);
        var patterns = admin.Get<WorkPatternService>();

        var draft = new WorkPattern { Name = "Mornings", CycleWeeks = 1 };
        draft.Segments.Add(new WorkPatternSegment { DayIndex = 0, Start = new TimeOnly(8, 0), End = new TimeOnly(12, 0) });
        Made(await patterns.CreateWorkPatternAsync(draft, WorkPatternWorkflowAttribute.SaveStep, Ct));
        await bus.DeliveredAsync().WaitAsync(Ct);
        Assert.Empty(heard);

        Made(await patterns.UpdateWorkPatternAndTransitionAsync((await patterns.LoadWorkPatternUpdaterAsync(pattern, Ct)).Value!, WorkPatternWorkflowAttribute.ArchiveStep, null, Ct));
        await bus.DeliveredAsync().WaitAsync(Ct);
        var change = Assert.Single(heard);
        Assert.Equal([adaRecord], change.StaffIds);
        Assert.Empty(change.JobIds);
    }

    private ServiceHost Shared(AssignmentScenario scenario, Guid userId, TenantChangeBus bus) =>
        ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(userId), services =>
        {
            AssignmentScenario.UseClock(services);
            services.AddSingleton(bus);
        });

    private static ConcurrentQueue<TenantChange> Listen(TenantChangeBus bus, Guid tenantId)
    {
        var heard = new ConcurrentQueue<TenantChange>();
        _ = bus.Subscribe(tenantId, change =>
        {
            heard.Enqueue(change);
            return Task.CompletedTask;
        });
        return heard;
    }

    private bool HeldAtOnce(Guid resource, Guid qualification)
    {
        using var reader = this.database.CreateSystemContext();
        reader.Database.OpenConnection();
        reader.Database.ExecuteSqlRaw("SET LOCK_TIMEOUT 0");
        try
        {
            return reader.ResourceQualifications.AsNoTracking().Any(row => row.ResourceId == resource && row.QualificationId == qualification);
        }
        catch (SqlException exception) when (exception.Number == 1222)
        {
            return false;
        }
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);
}
