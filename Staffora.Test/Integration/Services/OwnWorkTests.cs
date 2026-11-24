using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class OwnWorkTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly RealDatabase database;

    public OwnWorkTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task My_assignments_lists_the_readers_own_requests_and_assignments_only_and_hides_the_closed_ones_until_asked()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Own Assignments", Ct);
        var (ada, ben) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Ben", Ct));
        var coordinator = scenario.Coordinator;
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var (_, chemistry) = await scenario.StartedJobAsync(this.database, "Chemistry", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Thursday, new TimeOnly(10, 0), new TimeOnly(11, 0)));

        foreach (var refused in new[] { coordinator, scenario.Absence.Registrar })
        {
            Assert.Equal(SlotAssignmentService.NoView, (await OwnAsync(scenario, refused, new ListQuery())).Error);
        }

        var adaAlgebra = Made(await scenario.RequestAsync(this.database, coordinator, algebra[0], ada, Ct));
        var adaPhysics = Made(await scenario.RequestAsync(this.database, coordinator, physics[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaPhysics, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "Fully booked.")).IsSuccess);
        var benAlgebra = Made(await scenario.RequestAsync(this.database, coordinator, algebra[1], ben, Ct));

        await using (var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime()))
        {
            var given = await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.StaffMembers.TeamId, scenario.StaffMembers.Onboarded.Roles.Staff, scenario.TenantId);
            Assert.True(given.IsSuccess, given.Error);
        }

        var caseyChemistry = Made(await scenario.RequestAsync(this.database, coordinator, chemistry[0], coordinator, Ct));

        var page = await OwnAsync(scenario, ada, new ListQuery());
        Assert.Equal([(adaAlgebra, "Algebra: Lead", "Brightpath Learning", SlotAssignmentState.Requested)], page.Rows.Select(row => (row.Id, row.Label, row.Customer, row.State)));
        Assert.Equal("Your answer is needed", Assert.Single(page.Rows).NextLabel);
        Assert.Equal("2027-02-01 to 2027-06-11", Assert.Single(page.Rows).PeriodLabel);
        Assert.Equal(1, page.HiddenCounts[OwnAssignmentList.ShowClosed]);

        var all = await OwnAsync(scenario, ada, new ListQuery().WithToggle(OwnAssignmentList.ShowClosed, true));
        Assert.Equal([adaAlgebra, adaPhysics], all.Rows.Select(row => row.Id));
        Assert.Equal([adaPhysics], (await OwnAsync(scenario, ada, new ListQuery().WithFilter(OwnAssignmentList.Status, "declined"))).Rows.Select(row => row.Id));
        Assert.Equal([adaPhysics], (await OwnAsync(scenario, ada, new ListQuery { Search = "Phys" }.WithToggle(OwnAssignmentList.ShowClosed, true))).Rows.Select(row => row.Id));

        Assert.Equal([benAlgebra], (await OwnAsync(scenario, ben, new ListQuery().WithToggle(OwnAssignmentList.ShowClosed, true))).Rows.Select(row => row.Id));
        var casey = await OwnAsync(scenario, coordinator, new ListQuery().WithToggle(OwnAssignmentList.ShowClosed, true));
        Assert.Equal([(caseyChemistry, "Chemistry: Lead")], casey.Rows.Select(row => (row.Id, row.Label)));
    }

    [Fact]
    public async Task My_schedule_gives_a_week_of_the_readers_own_working_time_absences_and_occurrences_and_nothing_of_another_staff_members()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Own Week", Ct);
        var coordinator = scenario.Coordinator;
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2027, 1, 4), Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", new DateOnly(2027, 1, 4), Ct);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var (_, history) = await scenario.StartedJobAsync(this.database, "History", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Friday, new TimeOnly(12, 0), new TimeOnly(13, 0)));

        var adaAlgebra = Made(await scenario.RequestAsync(this.database, coordinator, algebra[0], ada, Ct));
        var adaHistory = Made(await scenario.AssignAsync(this.database, coordinator, history[0], ada, Ct));
        var adaPhysics = Made(await scenario.RequestAsync(this.database, coordinator, physics[0], ada, Ct));
        Assert.True((await scenario.StepAsync(this.database, ada, adaPhysics, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "No.")).IsSuccess);

        Made(await scenario.RequestAsync(this.database, coordinator, algebra[1], ben, Ct));
        await using (var benHost = scenario.HostOf(this.database, ben))
        {
            Made(await AbsenceScenario.ReportAsync(benHost.Get<ResourceAbsenceService>(), ben, AbsenceKind.SickLeave, new DateOnly(2027, 3, 9), new DateOnly(2027, 3, 9), Ct));
        }

        Guid wednesday;
        await using (var adaHost = scenario.HostOf(this.database, ada))
        {
            var absences = adaHost.Get<ResourceAbsenceService>();
            wednesday = Made(await AbsenceScenario.ReportAsync(absences, ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 10), Ct, start: new TimeOnly(13, 0)));
            var thursday = Made(await AbsenceScenario.ReportAsync(absences, ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 11), new DateOnly(2027, 3, 11), Ct));
            Assert.True((await AbsenceScenario.StepAsync(absences, thursday, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).IsSuccess);
        }

        var week = await WeekAsync(scenario, ada, new DateOnly(2027, 3, 10));
        Assert.Equal((new DateOnly(2027, 3, 8), "2027-03-08 to 2027-03-14", "40 h"), (week.Monday, week.Label, week.WorkedLabel));
        Assert.Equal(7, week.Days.Count);
        Assert.Equal(["08:00–16:00", "08:00–16:00", "08:00–16:00", "08:00–16:00", "08:00–16:00", "No working hours", "No working hours"], week.Days.Select(day => day.HoursLabel));
        Assert.Equal([(adaAlgebra, "Algebra", "Lead", "08:00–09:30", "You are asked")], week.Days[0].Occurrences.Select(occurrence => (occurrence.AssignmentId, occurrence.Job, occurrence.Slot, occurrence.TimesLabel, occurrence.StateLabel)));
        Assert.True(week.Days[1].IsQuiet);
        Assert.Equal([(wednesday, "Planned leave, waiting for approval", "From 13:00", false)], week.Days[2].Absences.Select(absence => (absence.AbsenceId, absence.Label, absence.TimesLabel, absence.Blocks)));
        Assert.True(week.Days[3].IsQuiet);
        Assert.Equal([(adaHistory, "History", "12:00–13:00", "You are assigned")], week.Days[4].Occurrences.Select(occurrence => (occurrence.AssignmentId, occurrence.Job, occurrence.TimesLabel, occurrence.StateLabel)));
        Assert.DoesNotContain(week.Days.SelectMany(day => day.Occurrences), occurrence => occurrence.Slot == "Assistant" || occurrence.Job == "Physics");

        var benWeek = await WeekAsync(scenario, ben, new DateOnly(2027, 3, 8));
        Assert.Equal([("Algebra", "Assistant")], benWeek.Days.SelectMany(day => day.Occurrences).Select(occurrence => (occurrence.Job, occurrence.Slot)));
        Assert.Equal([("Sick leave, reported", "All day", true)], benWeek.Days[1].Absences.Select(absence => (absence.Label, absence.TimesLabel, absence.Blocks)));
        Assert.Empty(benWeek.Days[2].Absences);

        var thisWeek = await WeekAsync(scenario, ada, null);
        Assert.Equal((new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9), true), (thisWeek.Monday, thisWeek.Today, thisWeek.IsCurrent));
        Assert.All(thisWeek.Days, day => Assert.True(day.IsQuiet));

        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            Assert.Equal(StaffAvailabilityService.NoOwnSchedule, (await host.Get<StaffAvailabilityService>().ReadOwnWeekAsync(null, Ct)).Error);
        }

        var pat = await scenario.StaffMembers.AddPersonAsync(this.database, "Pat", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, pat, Ct, Staffora.Core.Entities.Tenancy.RolePermission.ViewOwnSchedule);
        await using (var host = scenario.HostOf(this.database, pat))
        {
            Assert.Equal(StaffAvailabilityService.StaffNotFound, (await host.Get<StaffAvailabilityService>().ReadOwnWeekAsync(null, Ct)).Error);
        }
    }

    [Fact]
    public async Task An_impersonating_platform_owner_reads_exactly_the_staff_members_own_work_and_the_console_reads_none_paged_in_the_database()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Own Work Reach", Ct);
        var coordinator = scenario.Coordinator;
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2027, 1, 4), Ct);
        var ben = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ben", new DateOnly(2027, 1, 4), Ct);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"], AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)));
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(10, 0), new TimeOnly(11, 0)));
        var adaAlgebra = Made(await scenario.RequestAsync(this.database, coordinator, algebra[0], ada, Ct));
        var adaPhysics = Made(await scenario.RequestAsync(this.database, coordinator, physics[0], ada, Ct));
        var benAlgebra = Made(await scenario.RequestAsync(this.database, coordinator, algebra[1], ben, Ct));
        Assert.True((await scenario.StepAsync(this.database, ben, benAlgebra, SlotAssignmentWorkflowAttribute.DeclineStep, Ct, "No.")).IsSuccess);

        await using (var adaHost = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(adaHost.Get<ResourceAbsenceService>(), ada, AbsenceKind.PlannedLeave, new DateOnly(2027, 3, 5), new DateOnly(2027, 3, 9), Ct, start: new TimeOnly(13, 0), end: new TimeOnly(12, 0)));
        }

        await using (var benHost = scenario.HostOf(this.database, ben))
        {
            Made(await AbsenceScenario.ReportAsync(benHost.Get<ResourceAbsenceService>(), ben, AbsenceKind.SickLeave, new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 8), Ct));
        }

        var impersonating = scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(ada, scenario.TenantId);
        await using (var host = ServiceHost.Create(this.database, impersonating, AssignmentScenario.UseClock))
        {
            var assignments = host.Get<SlotAssignmentService>();
            host.ClearCommandLog();
            var own = await assignments.QueryOwnAssignmentsAsync(new ListQuery().WithToggle(OwnAssignmentList.ShowClosed, true), Ct);
            Assert.True(own.IsSuccess, own.Error);
            Assert.Equal(2, own.Value!.TotalCount);
            Assert.Equal(new[] { adaAlgebra, adaPhysics }.Order(), own.Value.Rows.Select(row => row.Id).Order());
            Assert.Contains(host.CommandLog, command => command.Contains("OFFSET", StringComparison.Ordinal) && command.Contains("FETCH NEXT", StringComparison.Ordinal));
            Assert.Empty((await assignments.QueryOwnAssignmentsAsync(new ListQuery { Search = "Brightpath" }.WithFilter(OwnAssignmentList.Status, "declined"), Ct)).Value!.Rows);

            var week = await host.Get<StaffAvailabilityService>().ReadOwnWeekAsync(new DateOnly(2027, 3, 14), Ct);
            Assert.True(week.IsSuccess, week.Error);
            Assert.Equal(new DateOnly(2027, 3, 8), week.Value!.Monday);
            Assert.Equal([("All day", "Planned leave, waiting for approval")], week.Value.Days[0].Absences.Select(absence => (absence.TimesLabel, absence.Label)));
            Assert.Equal(["Until 12:00"], week.Value.Days[1].Absences.Select(absence => absence.TimesLabel));
            Assert.All(week.Value.Days.Skip(2), day => Assert.Empty(day.Absences));
            Assert.Equal([(adaAlgebra, "Lead")], week.Value.Days[0].Occurrences.Select(occurrence => (occurrence.AssignmentId, occurrence.Slot)));
            Assert.Equal([adaPhysics], week.Value.Days[1].Occurrences.Select(occurrence => occurrence.AssignmentId));
        }

        var before = await WeekAsync(scenario, ada, new DateOnly(2027, 3, 1));
        Assert.Equal(["From 13:00", "All day", "All day"], before.Days.Skip(4).SelectMany(day => day.Absences).Select(absence => absence.TimesLabel));

        await using (var console = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.Platform.PlatformOwnerRuntime(), AssignmentScenario.UseClock))
        {
            var rows = await console.Get<SlotAssignmentService>().QueryOwnAssignmentsAsync(new ListQuery().WithToggle(OwnAssignmentList.ShowClosed, true), Ct);
            Assert.True(!rows.IsSuccess || !rows.Value!.Rows.Any());
            Assert.False((await console.Get<StaffAvailabilityService>().ReadOwnWeekAsync(new DateOnly(2027, 3, 8), Ct)).IsSuccess);
        }

        await using (var host = scenario.HostOf(this.database, ada))
        {
            var availability = host.Get<StaffAvailabilityService>();
            foreach (var day in new[] { DateOnly.MinValue, DateOnly.MinValue.AddDays(7), DateOnly.MaxValue.AddDays(-7), DateOnly.MaxValue })
            {
                Assert.Equal(StaffAvailabilityService.RangeOutOfCalendar, (await availability.ReadOwnWeekAsync(day, Ct)).Error);
            }

            foreach (var day in new[] { DateOnly.MinValue.AddDays(8), DateOnly.MaxValue.AddDays(-8) })
            {
                var read = await availability.ReadOwnWeekAsync(day, Ct);
                Assert.True(read.IsSuccess, $"{day:yyyy-MM-dd}: {read.Error}");
            }
        }
    }

    [Fact]
    public async Task My_schedule_shows_an_occurrence_only_inside_its_jobs_period_and_keeps_a_completed_jobs_occurrences_on_its_past_weeks()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Own Week Period", Ct);
        var ada = await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", new DateOnly(2027, 1, 4), Ct);

        var (job, slots) = await scenario.StartedJobAsync(this.database, "Short job", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 10), Ct, null,
            AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0)),
            AssignmentScenario.Occurrence(Weekday.Thursday, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        var assignment = Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using (var confirming = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Assert.True((await AssignmentScenario.StepJobAsync(confirming.Get<JobService>(), job, JobWorkflowAttribute.ConfirmStep, Ct)).IsSuccess);
        }

        var last = await WeekAsync(scenario, ada, new DateOnly(2027, 3, 8));
        Assert.Equal([("2027-03-08", "08:00–09:00")], last.Days.SelectMany(day => day.Occurrences.Select(occurrence => (Staffora.Core.Scheduling.ScheduleRules.DateText(day.Date), occurrence.TimesLabel))));

        await using (var clockHost = ServiceHost.Create(this.database, new Staffora.Core.Execution.RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2027, 3, 20, 12, 0, 0, TimeSpan.Zero)));
            services.AddScoped<Staffora.Core.Execution.IRuntimeExecutionContext, Staffora.Core.Execution.RuntimeExecutionContext>();
        }))
        {
            await clockHost.Get<JobClock>().RunOnceAsync(Ct);
        }

        Assert.Equal([SlotAssignmentState.Assigned], await AssignmentScenario.StatesAsync(this.database, Ct, assignment));
        var afterward = await WeekAsync(scenario, ada, new DateOnly(2027, 3, 1));
        Assert.Equal([("Short job", "You are assigned"), ("Short job", "You are assigned")], afterward.Days.SelectMany(day => day.Occurrences).Select(occurrence => (occurrence.Job, occurrence.StateLabel)));
    }

    private async Task<OwnPage> OwnAsync(AssignmentScenario scenario, Guid reader, ListQuery query)
    {
        await using var host = scenario.HostOf(this.database, reader);
        var page = await host.Get<SlotAssignmentService>().QueryOwnAssignmentsAsync(query, Ct);
        return new OwnPage(page.IsSuccess ? [.. page.Value!.Rows] : [], page.IsSuccess ? page.Value!.HiddenCounts : new Dictionary<string, int>(), page.IsSuccess ? null : page.Error);
    }

    private async Task<OwnScheduleWeek> WeekAsync(AssignmentScenario scenario, Guid reader, DateOnly? day)
    {
        await using var host = scenario.HostOf(this.database, reader);
        var week = await host.Get<StaffAvailabilityService>().ReadOwnWeekAsync(day, Ct);
        Assert.True(week.IsSuccess, week.Error);
        return week.Value!;
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private sealed record OwnPage(List<OwnAssignmentRow> Rows, IReadOnlyDictionary<string, int> HiddenCounts, string? Error);
}
