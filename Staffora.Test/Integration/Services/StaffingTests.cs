using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class StaffingTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 6, 11);

    private static readonly DateOnly WeekSix = new(2027, 3, 8);

    private readonly RealDatabase database;

    public StaffingTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_absent_staff_member_is_shown_blocked_with_the_dates_and_cannot_be_assigned_by_any_step_or_path()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Absent Staffing", Ct);
        var (ada, bo, cy) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant", "Reserve"]);
        var coordinator = scenario.Coordinator;

        var boAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[1], bo, Ct));
        await ReportSickAsync(scenario, ada, WeekSix);
        await ReportSickAsync(scenario, bo, WeekSix);

        var candidates = await CandidatesAsync(scenario, slots[0]);
        var adaRow = Assert.Single(candidates, row => row.Name == "Ada Example");
        Assert.True(adaRow.Verdict.IsBlocked);
        Assert.Equal($"Blocked on 1 of {adaRow.Verdict.Occurrences} occurrences", adaRow.AvailabilityLabel);
        Assert.Equal(["On sick leave (reported): 1 occurrence, 2027-03-08"], adaRow.Blocks);
        Assert.False(Assert.Single(candidates, row => row.Name == "Cy Example").Verdict.IsBlocked);

        var adaRecord = await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct);
        var refused = await scenario.AssignAsync(this.database, coordinator, slots[0], ada, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains($"The staff member is not free for 1 of the job's {adaRow.Verdict.Occurrences} remaining occurrences. On sick leave (reported): 1 occurrence, 2027-03-08.", refused.Error, StringComparison.Ordinal);
        Assert.Contains("2027-03-08", (await scenario.RequestAsync(this.database, coordinator, slots[0], ada, Ct)).Error, StringComparison.Ordinal);

        await using (var host = scenario.HostOf(this.database, coordinator))
        {
            var past = await host.Get<WorkflowService>().CreateAndTransitionAsync<SlotAssignmentState, SlotAssignment>(
                new SlotAssignment { TenantId = scenario.TenantId, State = SlotAssignmentState.Create, JobSlotId = slots[0], ResourceId = adaRecord },
                SlotAssignmentWorkflowAttribute.AssignStep,
                SlotAssignmentWorkflowQueries.WithWorkflowGraph,
                cancellationToken: Ct);
            Assert.False(past.IsSuccess);
            Assert.Contains("2027-03-08", past.Error, StringComparison.Ordinal);

            var unnamed = await host.Get<WorkflowService>().CreateAndTransitionAsync<SlotAssignmentState, SlotAssignment>(
                new SlotAssignment { State = SlotAssignmentState.Create, JobSlotId = slots[0], ResourceId = adaRecord },
                SlotAssignmentWorkflowAttribute.AssignStep,
                SlotAssignmentWorkflowQueries.WithWorkflowGraph,
                cancellationToken: Ct);
            Assert.False(unnamed.IsSuccess);
            Assert.Contains("On sick leave (reported): 1 occurrence, 2027-03-08", unnamed.Error, StringComparison.Ordinal);
        }

        Assert.Contains("2027-03-08", (await scenario.StepAsync(this.database, bo, boAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).Error, StringComparison.Ordinal);
        await using (var context = this.database.CreateSystemContext())
        {
            Assert.False(await context.SlotAssignments.AnyAsync(assignment => assignment.ResourceId == adaRecord, Ct));
        }

        var cyAssignment = Made(await scenario.RequestAsync(this.database, coordinator, slots[2], cy, Ct));
        Assert.True((await scenario.StepAsync(this.database, cy, cyAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);
        var leave = await RegisterLeaveAsync(scenario, cy, WeekSix);
        await using (var approving = scenario.HostOf(this.database, scenario.Absence.Approver))
        {
            var approved = await AbsenceScenario.StepAsync(approving.Get<ResourceAbsenceService>(), leave, ResourceAbsenceWorkflowAttribute.ApproveStep, Ct);
            Assert.True(approved.IsSuccess, approved.Error);
        }

        var pick = await scenario.StepAsync(this.database, coordinator, cyAssignment, SlotAssignmentWorkflowAttribute.PickStep, Ct);
        Assert.Contains("On planned leave: 1 occurrence, 2027-03-08", pick.Error, StringComparison.Ordinal);
        Assert.Equal(SlotAssignmentState.Accepted, (await AssignmentScenario.StoredAsync(this.database, cyAssignment, Ct))!.State);
    }

    [Fact]
    public async Task A_planned_leave_waiting_for_approval_blocks_nothing_and_a_canceled_absence_frees_the_staff_member()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Leave Staffing", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Biology", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);

        await RegisterLeaveAsync(scenario, ada, WeekSix);
        Assert.False(Assert.Single(await CandidatesAsync(scenario, slots[0]), row => row.Name == "Ada Example").Verdict.IsBlocked);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        var sick = await ReportSickAsync(scenario, bo, WeekSix);
        Assert.False((await scenario.AssignAsync(this.database, scenario.Coordinator, slots[1], bo, Ct)).IsSuccess);
        await using (var host = scenario.HostOf(this.database, bo))
        {
            Assert.True((await AbsenceScenario.StepAsync(host.Get<ResourceAbsenceService>(), sick, ResourceAbsenceWorkflowAttribute.CancelStep, Ct)).IsSuccess);
        }

        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[1], bo, Ct));
    }

    [Fact]
    public async Task A_staff_member_with_an_open_request_on_an_overlapping_job_shows_a_warning_and_is_still_assigned()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Warned Staffing", Ct);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);

        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(9, 0), new TimeOnly(11, 0)));
        var (_, drama) = await scenario.StartedJobAsync(this.database, "Drama", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(9, 0), new TimeOnly(11, 0)));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, physics[0], ada, Ct));
        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, drama[0], bo, Ct));

        var candidates = await CandidatesAsync(scenario, algebra[0]);
        var adaRow = Assert.Single(candidates, row => row.Name == "Ada Example");
        Assert.False(adaRow.Verdict.IsBlocked);
        Assert.StartsWith($"Free, a warning on {adaRow.Verdict.Occurrences} of {adaRow.Verdict.Occurrences} occurrences", adaRow.AvailabilityLabel, StringComparison.Ordinal);
        Assert.Contains(adaRow.Warnings, warning => warning.StartsWith($"{AvailabilityRules.RequestedReason} (Physics, Lead): {adaRow.Verdict.Occurrences} occurrences, 2027-02-01 to ", StringComparison.Ordinal));

        var boRow = Assert.Single(candidates, row => row.Name == "Bo Example");
        Assert.DoesNotContain(boRow.Warnings, warning => warning.StartsWith(AvailabilityRules.RequestedReason, StringComparison.Ordinal));
        Assert.Contains(boRow.Warnings, warning => warning.StartsWith(StaffingRules.OutsidePatternReason, StringComparison.Ordinal));

        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], ada, Ct));
    }

    [Fact]
    public async Task A_booking_on_another_job_at_the_same_time_blocks_its_job_named_and_an_occurrence_outside_the_pattern_warns()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Booked Staffing", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var bo = await scenario.Absence.AddWorkingStaffAsync(this.database, "Bo", PeriodStart, Ct);
        var (_, algebra) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct, ["Lead", "Assistant"]);
        var (_, physics) = await scenario.StartedJobAsync(this.database, "Physics", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(9, 0), new TimeOnly(11, 0)));

        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, physics[0], ada, Ct));
        var candidates = await CandidatesAsync(scenario, algebra[0]);
        var adaRow = Assert.Single(candidates, row => row.Name == "Ada Example");
        Assert.Equal(adaRow.Verdict.Occurrences, adaRow.Verdict.BlockedOccurrences);
        Assert.Equal([$"{AvailabilityRules.AssignedReason} (Physics, Lead): {adaRow.Verdict.Occurrences} occurrences, 2027-02-01 to 2027-06-07"], adaRow.Blocks);
        var boRow = Assert.Single(candidates, row => row.Name == "Bo Example");
        Assert.Equal(($"Free for all {boRow.Verdict.Occurrences} occurrences", 0), (boRow.AvailabilityLabel, boRow.Warnings.Count));
        Assert.Contains($"{AvailabilityRules.AssignedReason}: {adaRow.Verdict.Occurrences} occurrences", (await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], ada, Ct)).Error, StringComparison.Ordinal);

        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, algebra[0], bo, Ct));
        var assistant = Assert.Single(await CandidatesAsync(scenario, algebra[1]), row => row.Name == "Bo Example");
        Assert.Equal([$"{AvailabilityRules.AssignedReason} (Algebra, Lead): {assistant.Verdict.Occurrences} occurrences, 2027-02-01 to 2027-06-07"], assistant.Blocks);

        var (_, evening) = await scenario.StartedJobAsync(this.database, "Evening Spanish", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(18, 0), new TimeOnly(20, 0)));
        var late = Assert.Single(await CandidatesAsync(scenario, evening[0]), row => row.Name == "Bo Example");
        Assert.False(late.Verdict.IsBlocked);
        Assert.Equal([$"{StaffingRules.OutsidePatternReason}: {late.Verdict.Occurrences} occurrences, 2027-02-02 to 2027-06-08"], late.Warnings);
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, evening[0], bo, Ct));
    }

    [Fact]
    public async Task Candidates_are_ranked_by_the_defaults_and_an_archived_required_qualification_counts_as_required_and_met()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Ranked Staffing", Ct);
        var (ada, bo, cy, dee) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct), await scenario.AddStaffAsync(this.database, "Cy", Ct), await scenario.AddStaffAsync(this.database, "Dee", Ct));
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var qualifications = host.Get<QualificationService>();
        var mathematics = await QualificationAsync(qualifications, "Mathematics");
        var physics = await QualificationAsync(qualifications, "Physics");
        foreach (var (staff, held) in new[] { (ada, new[] { mathematics, physics }), (bo, [mathematics]), (dee, [mathematics, physics]) })
        {
            foreach (var qualification in held)
            {
                Assert.True((await qualifications.GiveQualificationAsync(staff, qualification, Ct)).IsSuccess);
            }
        }

        var (_, slots) = await JobAsync(scenario, "Mechanics", ("Lead", [mathematics, physics]));
        Assert.True((await qualifications.UpdateQualificationAndTransitionAsync((await qualifications.LoadQualificationUpdaterAsync(physics, Ct)).Value!, QualificationWorkflowAttribute.ArchiveStep, null, Ct)).IsSuccess);

        var (_, drama) = await scenario.StartedJobAsync(this.database, "Drama", PeriodStart, PeriodEnd, Ct, null, AssignmentScenario.Occurrence(Weekday.Tuesday, new TimeOnly(9, 0), new TimeOnly(11, 0)));
        var deeAssignment = Made(await scenario.RequestAsync(this.database, scenario.Coordinator, drama[0], dee, Ct));
        Assert.True((await scenario.StepAsync(this.database, dee, deeAssignment, SlotAssignmentWorkflowAttribute.AcceptStep, Ct)).IsSuccess);

        var ranked = await CandidatesAsync(scenario, slots[0]);
        Assert.Equal(
            [
                (1, "Ada Example", "All 2 required", "None"),
                (2, "Dee Example", "All 2 required", "1 job"),
                (3, "Bo Example", "1 of 2 required (missing: Physics)", "None"),
                (4, "Cy Example", "0 of 2 required (missing: Mathematics, Physics)", "None"),
            ],
            ranked.Select(row => (row.Rank, row.Name, row.MatchLabel, row.HeldLabel)));

        var options = (await host.Get<JobSlotService>().GetJobSlotEditorOptionsAsync(slots[0], Ct)).Value!;
        Assert.Equal("Mathematics, Physics (archived)", string.Join(", ", options.Staffing!.Required));
        var named = (await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery { Sort = StaffingCandidateList.SortName, Search = "e" }, Ct)).Value!.Rows;
        Assert.Equal([("Ada Example", 1), ("Bo Example", 3), ("Cy Example", 4), ("Dee Example", 2)], named.Select(row => (row.Name, row.Rank)));
    }

    [Fact]
    public async Task Only_a_coordinator_reads_candidates_and_another_tenants_slot_is_not_found()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Hidden Staffing", Ct);
        var other = await AssignmentScenario.CreateAsync(this.database, "Other Hidden Staffing", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        await other.AddStaffAsync(this.database, "Jon", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        await ReportSickAsync(scenario, ada, WeekSix);

        var ours = await CandidatesAsync(scenario, slots[0]);
        Assert.Contains(ours, row => row.Name == "Ada Example");
        Assert.DoesNotContain(ours, row => row.Name == "Jon Example");

        var (_, over) = await scenario.StartedJobAsync(this.database, "Last spring", new DateOnly(2026, 3, 2), new DateOnly(2026, 6, 12), Ct);

        await using (var host = scenario.HostOf(this.database, ada))
        {
            Assert.Equal(StaffingService.NoView, (await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery(), Ct)).Error);
            Assert.Equal(StaffingService.NoView, (await host.Get<StaffingService>().QuerySlotCandidatesAsync(over[0], new ListQuery(), Ct)).Error);
            Assert.False((await host.Get<JobSlotService>().GetJobSlotEditorOptionsAsync(slots[0], Ct)).IsSuccess);
        }

        Assert.Equal("No occurrences left in the period", Assert.Single(await CandidatesAsync(scenario, over[0])).AvailabilityLabel);

        var registrar = await scenario.StaffMembers.AddPersonAsync(this.database, "Reg", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.StaffMembers.Onboarded.Tenant, registrar, Ct, Staffora.Core.Entities.Tenancy.RolePermission.JobsView, Staffora.Core.Entities.Tenancy.RolePermission.JobsEdit);
        await using (var host = scenario.HostOf(this.database, registrar))
        {
            var options = await host.Get<JobSlotService>().GetJobSlotEditorOptionsAsync(slots[0], Ct);
            Assert.True(options.IsSuccess, options.Error);
            Assert.Null(options.Value!.Staffing);
            Assert.Equal(StaffingService.NoView, (await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery(), Ct)).Error);
        }

        await using (var host = other.HostOf(this.database, other.Coordinator))
        {
            Assert.Equal(JobSlotService.NotFound, (await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slots[0], new ListQuery(), Ct)).Error);
            Assert.Null((await host.Get<JobSlotService>().GetJobSlotEditorOptionsAsync(slots[0], Ct)).Value?.Staffing);
        }
    }

    [Fact]
    public async Task Only_staff_members_in_service_are_candidates_and_a_staff_member_back_in_service_is_one_again()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Service Staffing", Ct);
        var (ada, _) = (await scenario.AddStaffAsync(this.database, "Ada", Ct), await scenario.AddStaffAsync(this.database, "Bo", Ct));
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        Assert.Contains(await CandidatesAsync(scenario, slots[0]), row => row.Name == "Ada Example");

        await StepAccountAsync(scenario, ada, ApplicationUserWorkflowAttribute.DeactivateStep);
        var names = (await CandidatesAsync(scenario, slots[0])).Select(row => row.Name).ToList();
        Assert.DoesNotContain("Ada Example", names);
        Assert.Contains("Bo Example", names);

        await StepAccountAsync(scenario, ada, ApplicationUserWorkflowAttribute.ReactivateStep);
        Assert.Contains(await CandidatesAsync(scenario, slots[0]), row => row.Name == "Ada Example");
    }

    [Fact]
    public async Task A_page_of_candidates_reads_a_fixed_number_of_queries_for_5_and_for_50_staff_members()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Counted Staffing", Ct);
        var (_, slots) = await scenario.StartedJobAsync(this.database, "Algebra", PeriodStart, PeriodEnd, Ct);
        var staffMembers = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            staffMembers.Add(await scenario.AddStaffAsync(this.database, $"Few{index:00}", Ct));
        }

        await ReportSickAsync(scenario, staffMembers[0], WeekSix);
        var few = await CommandsOfAsync(scenario, slots[0], 5);
        for (var index = 5; index < 50; index++)
        {
            staffMembers.Add(await scenario.AddStaffAsync(this.database, $"Many{index:00}", Ct));
        }

        Made(await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], staffMembers[1], Ct));
        var many = await CommandsOfAsync(scenario, slots[0], 49);
        Assert.Equal(few, many);
        Assert.InRange(few, 1, 13);
    }

    private async Task<int> CommandsOfAsync(AssignmentScenario scenario, Guid slot, int expected)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var service = host.Get<JobSlotService>();
        Assert.True((await service.Staffing.QuerySlotCandidatesAsync(slot, new ListQuery(), Ct)).IsSuccess);
        host.ClearCommandLog();
        var page = await service.Staffing.QuerySlotCandidatesAsync(slot, new ListQuery { PageSize = 100 }, Ct);
        Assert.True(page.IsSuccess, page.Error);
        Assert.Equal(expected, page.Value!.TotalCount);
        return host.CommandLog.Count;
    }

    private async Task<IReadOnlyList<StaffingCandidateRow>> CandidatesAsync(AssignmentScenario scenario, Guid slot)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var page = await host.Get<JobSlotService>().Staffing.QuerySlotCandidatesAsync(slot, new ListQuery { PageSize = 100 }, Ct);
        Assert.True(page.IsSuccess, page.Error);
        return [.. page.Value!.Rows];
    }

    private async Task StepAccountAsync(AssignmentScenario scenario, Guid user, string step)
    {
        await using var owner = ServiceHost.Create(this.database, scenario.StaffMembers.Onboarded.OwnerRuntime());
        var stepped = await owner.Get<UserService>().StepUserAsync(user, step);
        Assert.True(stepped.IsSuccess, stepped.Error);
    }

    private async Task<Guid> ReportSickAsync(AssignmentScenario scenario, Guid staff, DateOnly day)
    {
        await using var host = scenario.HostOf(this.database, staff);
        return Made(await AbsenceScenario.ReportAsync(host.Get<ResourceAbsenceService>(), staff, AbsenceKind.SickLeave, day, day, Ct));
    }

    private async Task<Guid> RegisterLeaveAsync(AssignmentScenario scenario, Guid staff, DateOnly day)
    {
        await using var host = scenario.HostOf(this.database, scenario.Absence.Registrar);
        var id = Made(await AbsenceScenario.RegisterAsync(host.Get<ResourceAbsenceService>(), staff, AbsenceKind.PlannedLeave, day, day, Ct));
        Assert.Equal(ResourceAbsenceState.Requested, (await AbsenceScenario.StoredAsync(this.database, id, Ct))!.State);
        return id;
    }

    private async Task<Guid> QualificationAsync(QualificationService qualifications, string name) =>
        Made(await qualifications.CreateQualificationAsync(new Qualification { Name = name }, QualificationWorkflowAttribute.CreateStep, Ct));

    private async Task<(Guid Job, Guid[] Slots)> JobAsync(AssignmentScenario scenario, string name, params (string Slot, Guid[] Requires)[] slots)
    {
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var job = Made(await jobs.CreateJobAsync(new Job
        {
            Name = name,
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            TimeSlots = [AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))],
        }, JobWorkflowAttribute.RegisterStep, Ct));
        var ids = new List<Guid>();
        foreach (var (slot, requires) in slots)
        {
            ids.Add(Made(await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = slot, Qualifications = [.. requires.Select(qualification => new JobSlotQualification { QualificationId = qualification })] }, JobSlotWorkflowAttribute.AddStep, Ct)));
        }

        var started = await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep, Ct);
        Assert.True(started.IsSuccess, started.Error);
        return (job, [.. ids]);
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }
}
