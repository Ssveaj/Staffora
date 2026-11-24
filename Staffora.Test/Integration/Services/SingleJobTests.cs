using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Execution;
using Staffora.Core.Jobs;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;
using Staffora.Services.Scheduling;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class SingleJobTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Day = new(2027, 3, 3);
    private static readonly DateOnly Monday = new(2027, 3, 1);
    private static readonly TimeOnly Evening = new(18, 0);
    private static readonly TimeOnly Night = new(2, 0);

    private readonly RealDatabase database;

    public SingleJobTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_single_job_registers_with_its_day_and_times_and_a_recurring_one_refuses_start_and_end_times()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Single Register", Ct);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();

        var fresh = (await jobs.NewJobAsync(Ct)).Value!;
        var today = JobRules.LocalDay(AssignmentScenario.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(ServiceHost.PlatformTimeZone));
        Assert.Equal((JobSchedule.Single, today, today, (TimeOnly?)null, (TimeOnly?)null), (fresh.Schedule, fresh.PeriodStart, fresh.PeriodEnd, fresh.StartTime, fresh.EndTime));

        var missing = await jobs.CreateJobAsync(Draft(scenario, JobSchedule.Single, null, null), JobWorkflowAttribute.RegisterStep, Ct);
        Assert.Contains(JobRules.SingleStartMissing, missing.Error, StringComparison.Ordinal);
        Assert.Contains(JobRules.SingleEndMissing, missing.Error, StringComparison.Ordinal);
        Assert.Contains(JobRules.SingleTimesEqual, (await jobs.CreateJobAsync(Draft(scenario, JobSchedule.Single, Evening, Evening), JobWorkflowAttribute.RegisterStep, Ct)).Error, StringComparison.Ordinal);
        var weekly = Draft(scenario, JobSchedule.Single, Evening, Night);
        weekly.TimeSlots.Add(AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0)));
        Assert.Contains(JobRules.SingleHasWeeklyTimes, (await jobs.CreateJobAsync(weekly, JobWorkflowAttribute.RegisterStep, Ct)).Error, StringComparison.Ordinal);

        var recurring = Draft(scenario, JobSchedule.Recurring, Evening, Night);
        recurring.PeriodEnd = Day.AddMonths(1);
        Assert.Contains(JobRules.RecurringHasTimes, (await jobs.CreateJobAsync(recurring, JobWorkflowAttribute.RegisterStep, Ct)).Error, StringComparison.Ordinal);

        var draft = Draft(scenario, JobSchedule.Single, Evening, Night);
        draft.PeriodEnd = Day.AddMonths(3);
        var job = Made(await jobs.CreateJobAsync(draft, JobWorkflowAttribute.RegisterStep, Ct));
        var stored = await StoredAsync(job);
        Assert.Equal((JobSchedule.Single, Day, Day.AddDays(1), (TimeOnly?)Evening, (TimeOnly?)Night), (stored.Schedule, stored.PeriodStart, stored.PeriodEnd, stored.StartTime, stored.EndTime));

        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        (updater.Entity.StartTime, updater.Entity.EndTime) = (new TimeOnly(20, 0), new TimeOnly(23, 0));
        Made(await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveRegistrationStep, null, Ct));
        Assert.Equal(Day, (await StoredAsync(job)).PeriodEnd);

        Made(await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner" }, JobSlotWorkflowAttribute.AddStep, Ct));
        Made(await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep, Ct));
        var workflow = WorkflowRegistry.Default.Get<JobState, Job>();
        Assert.Equal(FieldAccessFlags.ReadWrite, workflow.GetFieldConfig(new Job { State = JobState.Coordinating }).GetFieldAccess(nameof(Job.StartTime)));
        Assert.Equal(FieldAccessFlags.ReadOnly, workflow.GetFieldConfig(new Job { State = JobState.Confirmed }).GetFieldAccess(nameof(Job.StartTime)));
        Assert.Equal(FieldAccessFlags.ReadOnly, workflow.GetFieldConfig(new Job { State = JobState.Confirmed }).GetFieldAccess(nameof(Job.Schedule)));

        await using var system = this.database.CreateSystemContext();
        var noStart = await Assert.ThrowsAnyAsync<Exception>(() => system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Jobs SET StartTime = NULL WHERE Id = {job}", Ct));
        Assert.Contains("CK_Jobs_ScheduleTimes", noStart.Message, StringComparison.Ordinal);
        var longer = await Assert.ThrowsAnyAsync<Exception>(() => system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Jobs SET PeriodEnd = {Day.AddDays(2)} WHERE Id = {job}", Ct));
        Assert.Contains("CK_Jobs_ScheduleTimes", longer.Message, StringComparison.Ordinal);
        var recurringWithTimes = await Assert.ThrowsAnyAsync<Exception>(() => system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Jobs SET Schedule = {(int)JobSchedule.Recurring} WHERE Id = {job}", Ct));
        Assert.Contains("CK_Jobs_ScheduleTimes", recurringWithTimes.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fake_clock_starts_a_single_job_at_its_start_time_and_completes_it_after_its_end_across_midnight()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Single Clock", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var (job, slots) = await SingleJobAsync(scenario, "Night cleaning", Day, Evening, Night, "Cleaner", "Helper");
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        Guid request;
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            request = Made(await coordinator.Get<SlotAssignmentService>().RequestStaffAsync(slots[1], await AssignmentScenario.ResourceOfAsync(this.database, bo, Ct), ResponseWindow.None, Ct));
            Made(await AssignmentScenario.StepJobAsync(coordinator.Get<JobService>(), job, JobWorkflowAttribute.ConfirmStep, Ct));
        }

        var account = SystemAccount.IdFor(scenario.TenantId);
        var clock = new FakeClock(At("2027-03-03T16:59:00Z"));
        await using var host = ClockHost(clock);
        var jobClock = host.Get<JobClock>();

        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.Confirmed, (await StoredAsync(job)).State);
        clock.Now = At("2027-03-03T17:00:00Z");
        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.InProgress, (await StoredAsync(job)).State);
        var started = (await HistoryAsync(job))[^1];
        Assert.Equal((JobWorkflowAttribute.PeriodStartsStep, account), (started.TransitionKey, started.ActorId));

        clock.Now = At("2027-03-04T00:59:00Z");
        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.InProgress, (await StoredAsync(job)).State);
        clock.Now = At("2027-03-04T01:00:00Z");
        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.Completed, (await StoredAsync(job)).State);
        Assert.Equal(JobWorkflowAttribute.PeriodEndsStep, (await HistoryAsync(job))[^1].TransitionKey);
        Assert.Equal(SlotAssignmentState.NotSelected, (await AssignmentScenario.StoredAsync(this.database, request, Ct))!.State);
        Assert.Contains(SlotAssignmentWorkflowAttribute.CloseRequestStep, await AssignmentScenario.HistoryAsync(this.database, request, Ct));

        var (late, _) = await SingleJobAsync(scenario, "Late cleaning", Day.AddDays(7), Evening, Night, "Cleaner");
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            Made(await AssignmentScenario.StepJobAsync(coordinator.Get<JobService>(), late, JobWorkflowAttribute.ConfirmStep, Ct));
        }

        clock.Now = At("2027-03-11T09:00:00Z");
        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.Completed, (await StoredAsync(late)).State);
        Assert.Equal([JobWorkflowAttribute.PeriodStartsStep, JobWorkflowAttribute.PeriodEndsStep], (await HistoryAsync(late)).TakeLast(2).Select(entry => entry.TransitionKey));
    }

    [Fact]
    public async Task A_single_order_arrives_as_a_single_job()
    {
        var portal = await PortalScenario.CreateAsync(this.database, "Single Ordered", Ct);
        var orderer = await portal.AddOrdererAsync(this.database, "Olive", Ct);
        await using var host = ServiceHost.Create(this.database, portal.RuntimeOf(orderer));
        var orders = host.Get<OrderService>();

        var fresh = (await orders.NewOrderAsync(Ct)).Value!;
        Assert.Equal(JobSchedule.Single, fresh.Schedule);
        (fresh.Name, fresh.PeriodStart) = ("Night cleaning", Day);
        Assert.Contains(JobRules.SingleStartMissing, (await orders.CreateOrderAsync(fresh, OrderWorkflowAttribute.SaveDraftStep, Ct)).Error, StringComparison.Ordinal);

        (fresh.StartTime, fresh.EndTime) = (Evening, Night);
        var order = Made(await orders.CreateOrderAsync(fresh, OrderWorkflowAttribute.SaveDraftStep, Ct));
        Made(await orders.UpdateOrderAndTransitionAsync((await orders.LoadOrderUpdaterAsync(order, Ct)).Value!, OrderWorkflowAttribute.SendStep, null, Ct));
        await using var system = this.database.CreateSystemContext();
        var job = await system.Jobs.AsNoTracking().SingleAsync(row => row.TenantId == portal.Supplier.TenantId, Ct);
        Assert.Equal((JobSchedule.Single, Day, Day.AddDays(1), (TimeOnly?)Evening, (TimeOnly?)Night, JobState.AwaitingCoordination), (job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, job.State));
        var details = await orders.ReadRequestAsync(order, Ct);
        Assert.Equal("2027-03-03 18:00-02:00 (+1 day)", details.Value!.PeriodLabel);
    }

    [Fact]
    public async Task The_board_and_the_staffing_dialog_agree_for_every_slot_and_staff_pair_of_a_single_job_past_midnight_and_a_recurring_job()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Single Parity", Ct);

        var from = new DateOnly(2026, 11, 2);
        var (ada, bo, cy) = (await scenario.Absence.AddWorkingStaffAsync(this.database, "Ada", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Cy", from, Ct));
        var (dee, eve, fay) = (await scenario.AddStaffAsync(this.database, "Dee", Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Eve", from, Ct), await scenario.Absence.AddWorkingStaffAsync(this.database, "Fay", from, Ct));

        var (_, nightSlots) = await SingleJobAsync(scenario, "Night cleaning", Day, Evening, Night, "Cleaner", "Helper");
        var (_, evening) = await SingleJobAsync(scenario, "Evening shift", Day, new TimeOnly(20, 0), new TimeOnly(23, 0), "Cleaner");
        var (_, smallHours) = await SingleJobAsync(scenario, "Small hours", Day.AddDays(1), new TimeOnly(1, 0), new TimeOnly(3, 0), "Cleaner");
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 26), Ct, ["Staff"]);
        var (_, biology) = await scenario.StartedJobAsync(this.database, "Biology", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 26), Ct, ["Staff"]);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, evening[0], ada, Ct));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, biology[0], bo, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, smallHours[0], cy, Ct));
        await using (var sick = scenario.HostOf(this.database, eve))
        {
            Made(await AbsenceScenario.ReportAsync(sick.Get<ResourceAbsenceService>(), eve, AbsenceKind.SickLeave, Day.AddDays(1), Day.AddDays(1), Ct));
        }

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var board = host.Get<CoordinationService>();
        var staffMembers = (await board.ReadStaffMembersAsync(Monday, new BoardFilter(), Ct)).Value!;
        var snapshot = (await host.Get<CoordinationService>().ReadAsync(Ct)).Value!;
        var bookings = await board.ReadBookingsAsync(staffMembers, snapshot, Ct);
        Assert.True(bookings.IsSuccess, bookings.Error);
        var onBoard = staffMembers.StaffMembers.Select(staff => staff.ResourceId).ToList();
        Assert.Equal(6, onBoard.Count);

        Assert.Subset(bookings.Value!.ToStaff.Select(occurrence => occurrence.SlotId).Distinct().ToList().ToHashSet(), new HashSet<Guid> { nightSlots[0], nightSlots[1], algebra[0] });
        var nightBars = bookings.Value.ToStaff.Where(occurrence => occurrence.SlotId == nightSlots[0]).ToList();
        Assert.Equal([(Day, "18:00-00:00"), (Day.AddDays(1), "00:00-02:00")], nightBars.Select(occurrence => (occurrence.Date, occurrence.Times)));
        Assert.All(nightBars, occurrence => Assert.Equal("2027-03-03 18:00-02:00 (+1 day)", occurrence.Occurrence));
        Assert.Null(bookings.Value.ToStaff.First(occurrence => occurrence.SlotId == algebra[0]).Occurrence);

        var marks = snapshot.DayMarks(Day, Day.AddDays(1));
        Assert.True(marks.ContainsKey(Day));
        Assert.True(marks.ContainsKey(Day.AddDays(1)));

        var compared = 0;
        foreach (var slot in new[] { nightSlots[0], nightSlots[1], algebra[0] })
        {
            var page = await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slot, new ListQuery { PageSize = 100 }, Ct);
            Assert.True(page.IsSuccess, page.Error);
            var candidates = page.Value!.Rows.ToDictionary(row => row.ResourceId);
            foreach (var staff in onBoard)
            {
                var pair = bookings.Value.Pair(slot, staff);
                Assert.NotNull(pair);
                var row = candidates[staff];
                Assert.Equal(row.Verdict.Summary, pair.Verdict.Summary);
                Assert.Equal(row.Verdict.IsBlocked, pair.Verdict.IsBlocked);
                Assert.Equal((row.Verdict.Occurrences, row.Verdict.BlockedOccurrences, row.Verdict.WarnedOccurrences), (pair.Verdict.Occurrences, pair.Verdict.BlockedOccurrences, pair.Verdict.WarnedOccurrences));
                Assert.Equal(row.Verdict.BlockGroups, pair.Verdict.BlockGroups);
                Assert.Equal(row.Verdict.WarningGroups, pair.Verdict.WarningGroups);
                compared++;
            }
        }

        Assert.Equal(3 * 6, compared);

        var records = new Dictionary<string, Guid>();
        foreach (var (name, user) in new[] { ("ada", ada), ("bo", bo), ("cy", cy), ("dee", dee), ("eve", eve), ("fay", fay) })
        {
            records[name] = await AssignmentScenario.ResourceOfAsync(this.database, user, Ct);
        }

        var cleaner = nightSlots[0];
        Assert.True(bookings.Value.Pair(cleaner, records["ada"])!.Verdict.IsBlocked);
        Assert.True(bookings.Value.Pair(cleaner, records["eve"])!.Verdict.IsBlocked);
        Assert.False(bookings.Value.Pair(cleaner, records["cy"])!.Verdict.IsBlocked);
        Assert.Contains(bookings.Value.Pair(cleaner, records["cy"])!.Verdict.WarningGroups, group => group.Reason.StartsWith("Asked to work", StringComparison.Ordinal));
        Assert.False(bookings.Value.Pair(cleaner, records["fay"])!.Verdict.IsBlocked);
        Assert.False(bookings.Value.Pair(cleaner, records["bo"])!.Verdict.IsBlocked);
        Assert.True(bookings.Value.Pair(algebra[0], records["bo"])!.Verdict.IsBlocked);
        Assert.False(bookings.Value.Pair(algebra[0], records["ada"])!.Verdict.IsBlocked);

        var held = Assert.Single(bookings.Value.OccurrencesOf(records["ada"], Day));
        Assert.Equal(("Evening shift", "20:00-23:00"), (held.Job, held.Times));
        Assert.Equal(1, bookings.Value.Pair(cleaner, records["fay"])!.Verdict.Occurrences);
    }

    private static Job Draft(AssignmentScenario scenario, JobSchedule schedule, TimeOnly? start, TimeOnly? end) => new()
    {
        Name = "Night cleaning",
        CustomerId = scenario.Customer,
        OrganizationUnitId = scenario.StaffMembers.RootId,
        Schedule = schedule,
        PeriodStart = Day,
        PeriodEnd = Day,
        StartTime = start,
        EndTime = end,
    };

    private async Task<(Guid Job, Guid[] Slots)> SingleJobAsync(AssignmentScenario scenario, string name, DateOnly day, TimeOnly start, TimeOnly end, params string[] slots)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var draft = Draft(scenario, JobSchedule.Single, start, end);
        (draft.Name, draft.PeriodStart, draft.PeriodEnd) = (name, day, day);
        var job = Made(await jobs.CreateJobAsync(draft, JobWorkflowAttribute.RegisterStep, Ct));
        var ids = new List<Guid>();
        foreach (var slot in slots)
        {
            ids.Add(Made(await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = slot }, JobSlotWorkflowAttribute.AddStep, Ct)));
        }

        Made(await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep, Ct));
        return (job, [.. ids]);
    }

    private ServiceHost ClockHost(FakeClock clock) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        });

    private async Task<Job> StoredAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Jobs.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<List<StateHistoryEntry>> HistoryAsync(Guid job)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityType == nameof(Job) && entry.EntityId == job).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(Ct);
    }

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);
}
