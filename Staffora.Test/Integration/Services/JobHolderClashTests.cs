using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class JobHolderClashTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);

    private readonly RealDatabase database;

    public JobHolderClashTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_occurrence_moved_onto_a_holders_other_job_is_refused_with_the_staff_member_and_dates_and_nothing_is_stored()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Clash Move", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (algebra, algebraSlots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        var (_, physicsSlots) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebraSlots[0], ada, Ct));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, physicsSlots[0], ada, Ct));

        var moved = await SaveAsync(scenario, algebra, job => job.TimeSlots.First().Weekday = Weekday.Tuesday, job => (job.TimeSlots.First().Start, job.TimeSlots.First().End) = (new TimeOnly(9, 0), new TimeOnly(11, 0)));
        Assert.False(moved.IsSuccess);
        Assert.Contains("Ada Example is not free for 8 of the 8 occurrences the change adds. Assigned to work a job: 8 occurrences, 2027-02-02 to 2027-03-23.", moved.Error, StringComparison.Ordinal);
        Assert.Equal([(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))], await OccurrencesAsync(algebra));
    }

    [Fact]
    public async Task A_period_made_longer_into_a_holders_absence_is_refused_while_an_edit_that_adds_only_free_occurrences_is_saved()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Clash Period", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (algebra, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using (var staff = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, new DateOnly(2027, 4, 5), new DateOnly(2027, 4, 5), Ct));
        }

        var longer = await SaveAsync(scenario, algebra, job => job.PeriodEnd = new DateOnly(2027, 4, 9));
        Assert.False(longer.IsSuccess);
        Assert.Contains("Ada Example is not free for 1 of the 2 occurrences the change adds. On sick leave (reported): 1 occurrence, 2027-04-05.", longer.Error, StringComparison.Ordinal);
        Assert.Equal(PeriodEnd, await PeriodEndAsync(algebra));

        Made(await SaveAsync(scenario, algebra, job => job.TimeSlots.Add(new JobTimeSlot { TenantId = job.TenantId, JobId = job.Id, Weekday = Weekday.Wednesday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) })));
        Made(await SaveAsync(scenario, algebra, job => job.PeriodEnd = new DateOnly(2027, 4, 2)));
        Assert.Equal(new DateOnly(2027, 4, 2), await PeriodEndAsync(algebra));
    }

    [Fact]
    public async Task An_occurrence_the_job_had_already_is_not_judged_again_so_an_at_risk_job_is_still_edited()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Clash Kept", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (algebra, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));
        await using (var staff = scenario.HostOf(this.database, ada))
        {
            Made(await AbsenceScenario.ReportAsync(staff.Get<ResourceAbsenceService>(), ada, AbsenceKind.SickLeave, new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 8), Ct));
        }

        Made(await SaveAsync(scenario, algebra, job => job.Description = "Weekly algebra"));
        Made(await SaveAsync(scenario, algebra, job => job.TimeSlots.First().End = new TimeOnly(9, 30)));

        var later = await SaveAsync(scenario, algebra, job => job.TimeSlots.First().End = new TimeOnly(10, 30));
        Assert.False(later.IsSuccess);
        Assert.Contains("On sick leave (reported): 1 occurrence, 2027-03-08", later.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_staff_member_who_said_yes_is_a_holder_too_and_a_job_nobody_holds_is_edited_freely()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Clash Yes", Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var (algebra, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        var (_, physicsSlots) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Thursday, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, physicsSlots[0], bo, Ct));
        var request = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], bo, Ct));
        Made(await scenario.StepAsync(this.database, bo, request, SlotAssignmentWorkflowAttribute.AcceptStep, Ct));

        var thursday = await SaveAsync(scenario, algebra, job => job.TimeSlots.Add(new JobTimeSlot { TenantId = job.TenantId, JobId = job.Id, Weekday = Weekday.Thursday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) }));
        Assert.False(thursday.IsSuccess);
        Assert.Contains("Bo Example is not free for 8 of the 8 occurrences the change adds. Assigned to work a job", thursday.Error, StringComparison.Ordinal);

        Made(await scenario.StepAsync(this.database, bo, request, SlotAssignmentWorkflowAttribute.WithdrawStep, Ct, comment: "Another job came up."));
        Made(await SaveAsync(scenario, algebra, job => job.TimeSlots.Add(new JobTimeSlot { TenantId = job.TenantId, JobId = job.Id, Weekday = Weekday.Thursday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) })));
    }

    private async Task<Result> SaveAsync(AssignmentScenario scenario, Guid job, params Action<Job>[] edits)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var updater = await jobs.LoadJobUpdaterAsync(job, Ct);
        Assert.True(updater.IsSuccess, updater.Error);
        foreach (var edit in edits)
        {
            edit(updater.Value!.Entity);
        }

        return await jobs.UpdateJobAndTransitionAsync(updater.Value!, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct);
    }

    private async Task<List<(Weekday, TimeOnly, TimeOnly)>> OccurrencesAsync(Guid job)
    {
        await using var context = this.database.CreateSystemContext();
        return [.. (await context.JobTimeSlots.AsNoTracking().Where(slot => slot.JobId == job).OrderBy(slot => slot.Weekday).ThenBy(slot => slot.Start).ToListAsync(Ct)).Select(slot => (slot.Weekday, slot.Start, slot.End))];
    }

    private async Task<DateOnly> PeriodEndAsync(Guid job)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Jobs.AsNoTracking().Where(row => row.Id == job).Select(row => row.PeriodEnd).SingleAsync(Ct);
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);
}
