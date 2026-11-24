using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Home;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class HomeTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Today = new(2026, 11, 9);
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private readonly RealDatabase database;

    public HomeTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task My_work_reads_the_readers_own_open_requests_next_assignments_and_absence_and_nothing_of_another_staff_members()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "My Work", Ct);
        var coordinator = scenario.Coordinator;
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", Today, Ct);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var (_, history) = await scenario.StartedJobAsync(this.database, "History", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(12, 0), new TimeOnly(13, 0)));

        var adaAlgebra = Made(await scenario.RequestAsync(this.database, coordinator, algebra[0], ada, Ct));
        var adaHistory = Made(await scenario.AssignAsync(this.database, coordinator, history[0], ada, Ct));
        var adaPhysics = Made(await scenario.RequestAsync(this.database, coordinator, physics[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaPhysics, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "No.")).IsSuccess);
        var benAlgebra = Made(await scenario.RequestAsync(this.database, coordinator, algebra[1], ben, Ct));

        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var absences = host.Get<ResourceAbsenceService>();
            Made(await AbsenceScenario.RegisterAsync(absences, ada, AbsenceKind.SickLeave, Today, Today.AddDays(1), Ct));
        }

        Guid march;
        await using (var host = scenario.HostOf(this.database, ada))
        {
            var absences = host.Get<ResourceAbsenceService>();
            march = Made(await AbsenceScenario.ReportAsync(absences, ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 10), Ct));
            var canceled = Made(await AbsenceScenario.ReportAsync(absences, ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 4, 1), new DateOnly(2027, 4, 1), Ct));
            Assert.True((await AbsenceScenario.StepAsync(absences, canceled, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).IsSuccess);
        }

        await using (var host = scenario.HostOf(this.database, ben))
        {
            Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ben, AbsenceKind.SickLeave, Today, Today, Ct));
        }

        var (adaWork, adaAbsence) = await MyWorkAsync(scenario.HostOf(this.database, ada));
        Assert.Equal([(adaAlgebra, "Algebra: Lead", "Your answer is needed")], adaWork.Requests.Select(row => (row.Id, row.Label, row.NextLabel)));
        Assert.Equal([(adaHistory, "History: Lead", "You are assigned")], adaWork.Next.Select(row => (row.Id, row.Label, row.NextLabel)));
        Assert.Equal((1, 1), (adaWork.RequestCount, adaWork.NextCount));
        Assert.Equal(Today, adaAbsence.Today);
        Assert.Equal([(AbsenceKind.SickLeave, ResourceAbsenceState.Approved, Today), (AbsenceKind.PlannedLeave, ResourceAbsenceState.Requested, new DateOnly(2027, 3, 10))], adaAbsence.Ahead.Select(row => (row.Kind, row.State, row.FirstDay)));
        Assert.Equal(march, adaAbsence.Ahead[1].Id);
        Assert.Equal((2, 1), (adaAbsence.AheadCount, adaAbsence.WaitingCount));
        Assert.Equal("Sick leave today, approved, until 2026-11-10", adaAbsence.Summary);

        var (benWork, benAbsence) = await MyWorkAsync(scenario.HostOf(this.database, ben));
        Assert.Equal([benAlgebra], benWork.Requests.Select(row => row.Id));
        Assert.Empty(benWork.Next);
        Assert.Equal([(AbsenceKind.SickLeave, ResourceAbsenceState.Requested)], benAbsence.Ahead.Select(row => (row.Kind, row.State)));
        Assert.Equal("Sick leave today, waiting for approval, until 2026-11-09", benAbsence.Summary);

        var impersonating = scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(ada, scenario.TenantId);
        var (seen, seenAbsence) = await MyWorkAsync(ServiceHost.Create(this.database, impersonating, AssignmentScenario.UseClock));
        Assert.Equal([adaAlgebra], seen.Requests.Select(row => row.Id));
        Assert.Equal([adaHistory], seen.Next.Select(row => row.Id));
        Assert.Equal(adaAbsence.Ahead.Select(row => row.Id), seenAbsence.Ahead.Select(row => row.Id));

        await using (var later = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.RuntimeOf(ada), services => services.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero)))))
        {
            var work = await later.Get<SlotAssignmentService>().ReadOwnWorkAsync(Ct);
            Assert.True(work.IsSuccess, work.Error);
            Assert.Equal([adaAlgebra], work.Value!.Requests.Select(row => row.Id));
            Assert.Equal(0, work.Value.NextCount);
            var absence = await later.Get<ResourceAbsenceService>().ReadOwnAbsenceStatusAsync(Ct);
            Assert.Equal(("No absence ahead", 0), (absence.Value!.Summary, absence.Value.AheadCount));
        }

        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            Assert.Equal(SlotAssignmentService.NoView, (await host.Get<SlotAssignmentService>().ReadOwnWorkAsync(Ct)).Error);
            Assert.Equal(ResourceAbsenceService.NoView, (await host.Get<ResourceAbsenceService>().ReadOwnAbsenceStatusAsync(Ct)).Error);
        }
    }

    [Fact]
    public async Task My_work_reads_at_most_five_of_each_the_earliest_period_first_and_counts_them_all_in_the_database()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "My Work Many", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);

        var asked = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            var start = PeriodStart.AddDays(7 * (6 - index));
            var (_, slots) = await scenario.StartedJobAsync(this.database, $"Job {index}", start, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8 + index, 0), new TimeOnly(8 + index, 30)));
            asked.Add(Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], ada, Ct)));
        }

        await using var host = scenario.HostOf(this.database, ada);
        host.ClearCommandLog();
        var work = await host.Get<SlotAssignmentService>().ReadOwnWorkAsync(Ct);
        Assert.True(work.IsSuccess, work.Error);
        Assert.Equal(7, work.Value!.RequestCount);
        Assert.Equal(OwnWork.Shown, work.Value.Requests.Count);

        Assert.Equal(asked.AsEnumerable().Reverse().Take(OwnWork.Shown), work.Value.Requests.Select(row => row.Id));
        Assert.Contains(host.CommandLog, command => command.Contains("TOP(", StringComparison.Ordinal) || command.Contains("FETCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_setup_checklist_counts_its_own_tenants_rows_only_all_to_do_when_fresh_and_all_done_when_set_up()
    {
        var setUp = await AssignmentScenario.CreateAsync(this.database, "Set Up", Ct);
        var ada = await setUp.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        await using (var admin = setUp.HostOf(this.database, setUp.Absence.Schedule.Admin))
        {
            var qualifications = admin.Get<QualificationService>();
            var maths = Made(await qualifications.CreateQualificationAsync(new Qualification { Name = "Mathematics" }, QualificationWorkflowAttribute.CreateStep, Ct));
            Assert.True((await qualifications.GiveQualificationAsync(ada, maths, Ct)).IsSuccess);
        }

        var fresh = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName("Fresh Setup"));

        var freshList = await ChecklistAsync(ServiceHost.Create(this.database, fresh.Onboarded.OwnerRuntime(), AssignmentScenario.UseClock));
        Assert.Equal(
            [
                (TenantHomeService.People, false, "1 active person", "/tenant/management/users"),
                (TenantHomeService.StaffMembers, false, "Nobody holds the Staff role yet", "/staffing/staff"),
                (TenantHomeService.Qualifications, false, "0 qualifications in the catalogue, and 0 of 0 staff members hold one", "/staffing/qualifications"),
                (TenantHomeService.WorkPatterns, false, "0 published work patterns, and 0 of 0 staff members have one", "/staffing/work-patterns"),
                (TenantHomeService.Customers, false, "0 active customers", "/staffing/customers"),
            ],
            freshList.Items.Select(item => (item.Key, item.Done, item.Detail, item.Path)));
        Assert.Equal((0, false), (freshList.DoneCount, freshList.IsComplete));

        var setUpList = await ChecklistAsync(ServiceHost.Create(this.database, setUp.StaffMembers.Onboarded.OwnerRuntime(), AssignmentScenario.UseClock));
        Assert.All(setUpList.Items, item => Assert.True(item.Done, item.Key));
        Assert.True(setUpList.IsComplete);
        Assert.Equal("1 qualification in the catalogue, and 1 of 1 staff members hold one", setUpList.Items.Single(item => item.Key == TenantHomeService.Qualifications).Detail);
        Assert.Equal("1 published work pattern, and 1 of 1 staff members have one", setUpList.Items.Single(item => item.Key == TenantHomeService.WorkPatterns).Detail);
        Assert.Equal("1 active customer", setUpList.Items.Single(item => item.Key == TenantHomeService.Customers).Detail);

        var impersonating = setUp.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(setUp.StaffMembers.Onboarded.Tenant.OwnerId, setUp.TenantId);
        Assert.Equal(setUpList.Items, (await ChecklistAsync(ServiceHost.Create(this.database, impersonating, AssignmentScenario.UseClock))).Items);
        await using (var console = ServiceHost.Create(this.database, setUp.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), AssignmentScenario.UseClock))
        {
            Assert.Equal(TenantHomeService.ChecklistNoTenant, (await console.Get<TenantHomeService>().ReadSetupChecklistAsync(Ct)).Error);
        }
    }

    [Fact]
    public async Task The_setup_checklist_shows_an_item_only_to_a_reader_of_its_rows_and_is_refused_without_TenantView()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Checklist Readers", Ct);
        var tenant = scenario.StaffMembers.Onboarded.Tenant;

        var admin = await ChecklistAsync(scenario.HostOf(this.database, scenario.Absence.Schedule.Admin));
        Assert.Equal([TenantHomeService.People, TenantHomeService.StaffMembers, TenantHomeService.Qualifications, TenantHomeService.WorkPatterns], admin.Items.Select(item => item.Key));

        var keeper = await scenario.StaffMembers.AddPersonAsync(this.database, "Kit", Ct);
        await TwoTenantScenario.GrantAsync(this.database, tenant, keeper, Ct, RolePermission.TenantView, RolePermission.CustomersView);
        Assert.Equal([TenantHomeService.Customers], (await ChecklistAsync(scenario.HostOf(this.database, keeper))).Items.Select(item => item.Key));

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        Assert.Equal(TenantHomeService.ChecklistNoView, (await host.Get<TenantHomeService>().ReadSetupChecklistAsync(Ct)).Error);
    }

    [Fact]
    public async Task The_administration_home_reads_each_part_for_a_reader_of_its_rows_only_and_counts_as_the_platform_overview_does()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Admin Home Reads", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        var (_, week) = await scenario.StartedJobAsync(this.database, "Windows", Today, Today.AddDays(4), Ct, ["Front", "Back"], AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, week[0], ada, Ct));
        await using (var host = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 10), Ct));
        }

        var owner = await AdministrationAsync(ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime(), AssignmentScenario.UseClock));
        Assert.Equal(new HomeStaffing(JobsThisWeek: 1, WeekSlotsHeld: 1, WeekSlotsOpen: 1, OpenSlots: 2, OpenSlotsSoon: 1), owner.Staffing);
        Assert.Equal(new HomePeople(1, 0), owner.People);
        Assert.Equal((0, 1), (owner.Absence!.AbsentTodayCount, owner.Absence.WaitingCount));
        Assert.Equal((1, 0), (owner.Customers!.Active, owner.Customers.WithPortal));
        Assert.NotEmpty(owner.Activity!);
        Assert.All(owner.Activity!, step => Assert.Contains(step.Job, new[] { "Windows", "Algebra" }));
        Assert.False(string.IsNullOrWhiteSpace(owner.FirstName));

        await using (var console = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), AssignmentScenario.UseClock))
        {
            var overview = await console.Get<Staffora.Services.Platform.PlatformConsoleService>().GetOverviewAsync(Ct);
            Assert.True(overview.IsSuccess, overview.Error);
            var row = overview.Value!.Tenants.Single(tenant => tenant.Id == scenario.TenantId);
            Assert.Equal((row.JobsThisWeek, row.OpenSlots), (owner.Staffing!.JobsThisWeek, owner.Staffing.OpenSlots));

            Assert.Equal(TenantHomeService.NoTenant, (await console.Get<TenantHomeService>().ReadAdministrationAsync(Ct)).Error);
        }

        var admin = await AdministrationAsync(scenario.HostOf(this.database, scenario.Absence.Schedule.Admin));
        Assert.Equal((false, true, false, false, false), (admin.Staffing is not null, admin.People is not null, admin.Absence is not null, admin.Customers is not null, admin.Activity is not null));

        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Assert.Equal(TenantHomeService.NoView, (await coordinator.Get<TenantHomeService>().ReadAdministrationAsync(Ct)).Error);
        }

        await using (var staff = scenario.HostOf(this.database, ada))
        {
            Assert.Equal("Ada", (await staff.Get<TenantHomeService>().ReadFirstNameAsync(Ct)).Value);
        }
    }

    [Fact]
    public async Task The_coordinators_home_reads_the_answers_today_and_the_approvals_for_a_coordinator_only_and_counts_as_the_administrators_does()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Coordinator Home Reads", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", Today, Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", Today, Ct);
        var cam = await scenario.Absence.AddWorkingStaffAsync(this.database, "Cam", Today, Ct);
        await scenario.AddStaffAsync(this.database, "Dee", Ct);
        var (windows, week) = await scenario.StartedJobAsync(this.database, "Windows", Today, Today.AddDays(4), Ct, ["Front", "Back"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(13, 0), new TimeOnly(15, 0)));
        var (_, later) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, week[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, week[1], ben, Ct));
        var yes = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, later[0], cam, Ct));
        Assert.True((await scenario.StepAsync(this.database, cam, yes, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        Guid leave;
        await using (var host = scenario.HostOf(this.database, ada))
        {
            leave = Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 10), Ct));
        }

        var home = await CoordinatorAsync(scenario.HostOf(this.database, scenario.Coordinator));
        Assert.False(string.IsNullOrWhiteSpace(home.FirstName));
        Assert.False(string.IsNullOrWhiteSpace(home.TenantName));
        Assert.Equal(Today, home.Today);

        var owner = await AdministrationAsync(ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime(), AssignmentScenario.UseClock));
        Assert.Equal(owner.Staffing, home.Staffing);
        Assert.Equal(new HomeStaffing(JobsThisWeek: 1, WeekSlotsHeld: 1, WeekSlotsOpen: 1, OpenSlots: 2, OpenSlotsSoon: 1), home.Staffing);

        Assert.Equal((1, 1), (home.Answers.Unanswered, home.Answers.YesCount));
        var waiting = Assert.Single(home.Answers.Yeses);
        Assert.Equal((yes, "Algebra", PeriodStart), (waiting.AssignmentId, waiting.Job, waiting.PeriodStart));
        Assert.StartsWith("Cam", waiting.Staff, StringComparison.Ordinal);

        var today = Assert.Single(home.TodayJobs);
        Assert.Equal((windows, "13:00-15:00", 2, 1, "1 of 2 held"), (today.JobId, today.TimesLabel, today.Slots, today.Held, today.HeldLabel));

        Assert.Equal([leave], home.Approvals!.Waiting.Select(row => row.Id));
        Assert.Equal(1, home.Approvals.WaitingCount);
        Assert.Null(home.Portal);

        Assert.Equal(1, home.WithoutWorkPattern);
        Assert.Equal(owner.People!.WithoutWorkPattern, home.WithoutWorkPattern);

        var pat = await scenario.StaffMembers.AddPersonAsync(this.database, "Pat", Ct);
        await using (var owners = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var planner = Made(await owners.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Planner", RolePermission.ViewCoordination), Ct));
            Assert.True((await owners.Get<RoleHoldingService>().AssignUserToRoleAsync(pat, scenario.StaffMembers.RootId, planner, scenario.TenantId)).IsSuccess);
        }

        var planned = await CoordinatorAsync(scenario.HostOf(this.database, pat));
        Assert.Null(planned.Approvals);
        Assert.Null(planned.WithoutWorkPattern);
        Assert.Equal((home.Staffing, home.Answers.Unanswered, home.Answers.YesCount, home.TodayJobs.Count), (planned.Staffing, planned.Answers.Unanswered, planned.Answers.YesCount, planned.TodayJobs.Count));

        var impersonating = scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.Coordinator, scenario.TenantId);
        var seen = await CoordinatorAsync(ServiceHost.Create(this.database, impersonating, AssignmentScenario.UseClock));
        Assert.Equal((home.FirstName, home.Answers.YesCount, home.Approvals.WaitingCount), (seen.FirstName, seen.Answers.YesCount, seen.Approvals?.WaitingCount));

        var (commands, _) = await CountedCoordinatorAsync(scenario);
        await scenario.AddStaffAsync(this.database, "Eve", Ct);
        await scenario.AddStaffAsync(this.database, "Fay", Ct);
        var (grownCommands, grown) = await CountedCoordinatorAsync(scenario);
        Assert.Equal(3, grown.WithoutWorkPattern);
        Assert.Equal(commands, grownCommands);

        await using (var staff = scenario.HostOf(this.database, ada))
        {
            Assert.Equal(TenantHomeService.NoCoordination, (await staff.Get<TenantHomeService>().ReadCoordinatorAsync(Ct)).Error);
        }

        await using (var console = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), AssignmentScenario.UseClock))
        {
            Assert.False((await console.Get<TenantHomeService>().ReadCoordinatorAsync(Ct)).IsSuccess);
        }
    }

    private static async Task<CoordinatorHome> CoordinatorAsync(ServiceHost host)
    {
        await using (host)
        {
            var home = await host.Get<TenantHomeService>().ReadCoordinatorAsync(Ct);
            Assert.True(home.IsSuccess, home.Error);
            return home.Value!;
        }
    }

    private async Task<(int Commands, CoordinatorHome Home)> CountedCoordinatorAsync(AssignmentScenario scenario)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        host.ClearCommandLog();
        var home = await host.Get<TenantHomeService>().ReadCoordinatorAsync(Ct);
        Assert.True(home.IsSuccess, home.Error);
        return (host.CommandLog.Count, home.Value!);
    }

    private static async Task<AdministrationHome> AdministrationAsync(ServiceHost host)
    {
        await using (host)
        {
            var home = await host.Get<TenantHomeService>().ReadAdministrationAsync(Ct);
            Assert.True(home.IsSuccess, home.Error);
            return home.Value!;
        }
    }

    private static async Task<(OwnWork Work, OwnAbsenceStatus Absence)> MyWorkAsync(ServiceHost host)
    {
        await using (host)
        {
            var work = await host.Get<SlotAssignmentService>().ReadOwnWorkAsync(Ct);
            Assert.True(work.IsSuccess, work.Error);
            var absence = await host.Get<ResourceAbsenceService>().ReadOwnAbsenceStatusAsync(Ct);
            Assert.True(absence.IsSuccess, absence.Error);
            return (work.Value!, absence.Value!);
        }
    }

    private static async Task<SetupChecklist> ChecklistAsync(ServiceHost host)
    {
        await using (host)
        {
            var checklist = await host.Get<TenantHomeService>().ReadSetupChecklistAsync(Ct);
            Assert.True(checklist.IsSuccess, checklist.Error);
            return checklist.Value!;
        }
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
