using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public sealed partial class JobLifecycleTests
{
    private const string NoSaveAccess = "The step 'Save' needs the permission 'Edit Jobs', which you do not have.";

    [Fact]
    public async Task A_step_carries_edits_only_for_a_user_who_may_save_the_job_through_the_service_and_past_it()
    {
        var (scenario, coordinator) = await ArrangeAsync("Edit Authority Job Staffing");
        Guid job;
        Guid contact;
        Guid branch;
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            branch = await owner.Get<OrganizationService>().CreateUnitIdAsync("Branch South", OrganizationUnitType.Branch, scenario.RootId, Ct);
        }

        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            Guid customer;
            (customer, contact) = await ActiveCustomerAsync(keeper, "Authority School", "Nora", "Vik");
            job = await RegisterAsync(keeper.Get<JobService>(), "Statistics", customer, contact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            await AddSlotAsync(keeper.Get<JobService>().Slots, job, "Lead");
        }

        var registrar = await scenario.AddPersonAsync(this.database, "Rita", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, registrar, Ct, RolePermission.JobsView, RolePermission.JobsCreate);
        var handOverRefusal = TransitionRefusals.EditsRequireSaveAccess(nameof(Job), "Hand over", NoSaveAccess);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(registrar)))
        {
            var jobs = host.Get<JobService>();
            Assert.Equal(handOverRefusal, (await jobs.UpdateJobAndTransitionAsync(Edited(await jobs.LoadJobUpdaterAsync(job, Ct), branch), JobWorkflowAttribute.HandOverStep, null, Ct)).Error);
            Assert.Equal(handOverRefusal, (await host.Get<WorkflowService>().SaveAndTransitionAsync<JobState, Job>(Edited(await jobs.LoadJobUpdaterAsync(job, Ct), branch), JobWorkflowAttribute.HandOverStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
            await AssertUnchangedAsync(job, "Statistics", JobState.Registration, contact, scenario.RootId);

            Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.HandOverStep)).IsSuccess);
        }

        var staffer = await scenario.AddPersonAsync(this.database, "Stig", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, staffer, Ct, RolePermission.JobsView, RolePermission.PerformCoordination);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(staffer)))
        {
            var jobs = host.Get<JobService>();
            var workflows = host.Get<WorkflowService>();
            var startRefusal = TransitionRefusals.EditsRequireSaveAccess(nameof(Job), "Start coordination", NoSaveAccess);
            Assert.Equal(startRefusal, (await jobs.UpdateJobAndTransitionAsync(Described(await jobs.LoadJobUpdaterAsync(job, Ct)), JobWorkflowAttribute.StartStep, null, Ct)).Error);
            Assert.Equal(startRefusal, (await workflows.SaveAndTransitionAsync<JobState, Job>(Described(await jobs.LoadJobUpdaterAsync(job, Ct)), JobWorkflowAttribute.StartStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
            await AssertUnchangedAsync(job, "Statistics", JobState.AwaitingCoordination, contact, scenario.RootId);
            Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.StartStep)).IsSuccess);

            var confirmRefusal = TransitionRefusals.EditsRequireSaveAccess(nameof(Job), "Confirm", NoSaveAccess);
            Assert.Equal(confirmRefusal, (await jobs.UpdateJobAndTransitionAsync(Moved(await jobs.LoadJobUpdaterAsync(job, Ct)), JobWorkflowAttribute.ConfirmStep, null, Ct)).Error);
            Assert.Equal(confirmRefusal, (await workflows.SaveAndTransitionAsync<JobState, Job>(Moved(await jobs.LoadJobUpdaterAsync(job, Ct)), JobWorkflowAttribute.ConfirmStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
            await AssertUnchangedAsync(job, "Statistics", JobState.Coordinating, contact, scenario.RootId);
            Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.ConfirmStep)).IsSuccess);
            Assert.Equal(JobState.Confirmed, (await StoredJobAsync(job)).State);
        }

        var editor = await scenario.AddPersonAsync(this.database, "Elin", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, editor, Ct, RolePermission.JobsView, RolePermission.JobsEdit, RolePermission.PerformCoordination);
        Guid second;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var (customer, _) = await ActiveCustomerAsync(keeper, "Second Authority School", "Olle", "Ek");
            second = await RegisterAsync(keeper.Get<JobService>(), "Probability", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            await AddSlotAsync(keeper.Get<JobService>().Slots, second, "Lead");
        }

        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(editor)))
        {
            var jobs = host.Get<JobService>();
            var started = await jobs.UpdateJobAndTransitionAsync(Described(await jobs.LoadJobUpdaterAsync(second, Ct)), JobWorkflowAttribute.StartFromRegistrationStep, null, Ct);
            Assert.True(started.IsSuccess, started.Error);
        }

        var stored = await StoredJobAsync(second);
        Assert.Equal((JobState.Coordinating, "Started with its description.", 2, editor), (stored.State, stored.Description, stored.TimeSlots.Count, stored.CoordinatorUserId));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_qualification_archived_while_a_slot_requires_it_anew_is_judged_inside_the_slots_step_after_the_archive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Racing Slot Staffing");
        Guid slot;
        Guid music;
        await using (var setup = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var (customer, _) = await ActiveCustomerAsync(setup, "Racing Slot School", "Ulf", "Sand");
            var job = await RegisterAsync(setup.Get<JobService>(), "Choir", customer, null, scenario.RootId, Occurrence(Weekday.Tuesday, 15, 0, 16, 0));
            slot = await AddSlotAsync(setup.Get<JobService>().Slots, job, "Conductor");
            music = await QualificationAsync(setup, "Music");
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var archiving = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator), services => services.AddSingleton<IStepPreparation>(new PauseOnce(typeof(Qualification), entered, gate)));
        var qualifications = archiving.Get<QualificationService>();
        var archiveUpdater = (await qualifications.LoadQualificationUpdaterAsync(music, cancellationToken)).Value!;
        var archive = qualifications.UpdateQualificationAndTransitionAsync(archiveUpdater, QualificationWorkflowAttribute.ArchiveStep, null, cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        await using var saving = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var slots = saving.Get<JobService>().Slots;
        var adding = (await slots.LoadJobSlotUpdaterAsync(slot, cancellationToken)).Value!;
        adding.Entity.Qualifications.Add(new JobSlotQualification { TenantId = scenario.TenantId, JobSlotId = slot, QualificationId = music });
        var save = slots.UpdateJobSlotAndTransitionAsync(adding, JobSlotWorkflowAttribute.SaveStep, null, cancellationToken);
        await WaitForApplicationLockWaitAsync(save);
        Assert.False(save.IsCompleted, "The slot's save did not wait for the job lock the archive holds.");

        gate.SetResult();
        Assert.True((await archive).IsSuccess);
        var saved = await save;
        Assert.False(saved.IsSuccess);
        Assert.Equal(JobSlotWorkflowAttribute.QualificationActiveReason, saved.Error);
        await using var context = this.database.CreateSystemContext();
        Assert.False(await context.JobSlotQualifications.AnyAsync(required => required.JobSlotId == slot, cancellationToken));
    }

    [Fact]
    public async Task Past_the_service_a_slot_requires_only_an_active_qualification_anew_of_its_own_tenant_and_keeps_an_archived_one()
    {
        var (scenario, coordinator) = await ArrangeAsync("Engine Slot Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var workflows = host.Get<WorkflowService>();
        var (customer, _) = await ActiveCustomerAsync(host, "Engine Slot School", "Vera", "Lund");
        var job = await RegisterAsync(jobs, "Drawing", customer, null, scenario.RootId, Occurrence(Weekday.Thursday, 10, 0, 11, 0));
        var art = await QualificationAsync(host, "Art");
        var music = await QualificationAsync(host, "Music");
        var kept = await AddSlotAsync(jobs.Slots, job, "Lead", art);
        await ArchiveQualificationAsync(host, art);
        await ArchiveQualificationAsync(host, music);

        var draft = new JobSlot { TenantId = scenario.TenantId, State = JobSlotState.Create, JobId = job, Name = "Singer", Qualifications = [new JobSlotQualification { TenantId = scenario.TenantId, QualificationId = music }] };
        Assert.Equal(JobSlotWorkflowAttribute.QualificationActiveReason, (await workflows.CreateAndTransitionAsync<JobSlotState, JobSlot>(draft, JobSlotWorkflowAttribute.AddStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);

        var adding = (await jobs.Slots.LoadJobSlotUpdaterAsync(kept, Ct)).Value!;
        adding.Entity.Qualifications.Add(new JobSlotQualification { TenantId = scenario.TenantId, JobSlotId = kept, QualificationId = music });
        Assert.Equal(JobSlotWorkflowAttribute.QualificationActiveReason, (await workflows.SaveAndTransitionAsync<JobSlotState, JobSlot>(adding, JobSlotWorkflowAttribute.SaveStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
        var renaming = (await jobs.Slots.LoadJobSlotUpdaterAsync(kept, Ct)).Value!;
        renaming.Entity.Name = "Lead staff member";
        var renamed = await workflows.SaveAndTransitionAsync<JobSlotState, JobSlot>(renaming, JobSlotWorkflowAttribute.SaveStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.True(renamed.IsSuccess, renamed.Error);
        var stored = (await StoredSlotsAsync(job)).Single(row => row.Id == kept);
        Assert.Equal("Lead staff member", stored.Name);
        Assert.Equal([art], stored.Qualifications.Select(required => required.QualificationId));
        Assert.Single(await StoredSlotsAsync(job));

        var other = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName("Other Engine Slot Staffing"));
        Guid foreign;
        await using (var otherHost = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime()))
        {
            foreign = await QualificationAsync(otherHost, "Dance");
        }

        var spy = new JobSlot { TenantId = scenario.TenantId, State = JobSlotState.Create, JobId = job, Name = "Dancer", Qualifications = [new JobSlotQualification { TenantId = scenario.TenantId, QualificationId = foreign }] };
        var missing = new JobSlot { TenantId = scenario.TenantId, State = JobSlotState.Create, JobId = job, Name = "Dancer", Qualifications = [new JobSlotQualification { TenantId = scenario.TenantId, QualificationId = Guid.NewGuid() }] };
        var spied = await workflows.CreateAndTransitionAsync<JobSlotState, JobSlot>(spy, JobSlotWorkflowAttribute.AddStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Equal((await workflows.CreateAndTransitionAsync<JobSlotState, JobSlot>(missing, JobSlotWorkflowAttribute.AddStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error, spied.Error);
        Assert.False(spied.IsSuccess);
        Assert.DoesNotContain((await StoredSlotsAsync(job)).Select(row => row.Name), name => name is "Singer" or "Dancer");
    }

    [Fact]
    public async Task A_registration_past_the_service_that_sets_a_coordinator_or_a_cross_tenant_link_is_refused()
    {
        var (scenario, coordinator) = await ArrangeAsync("Ahead Job Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var workflows = host.Get<WorkflowService>();
        var (customer, _) = await ActiveCustomerAsync(host, "Ahead School", "Ylva", "Dal");

        foreach (var set in new Action<Job>[]
        {
            draft => draft.CoordinatorUserId = scenario.Onboarded.OwnerId,
            draft => draft.CrossTenantId = Guid.NewGuid(),
            draft => draft.LinkedCrossTenantId = Guid.NewGuid(),
        })
        {
            var draft = Draft("Ahead job", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            draft.TenantId = scenario.TenantId;
            set(draft);
            Assert.Equal(JobWorkflowAttribute.NothingSetAheadReason, (await workflows.CreateAndTransitionAsync<JobState, Job>(draft, JobWorkflowAttribute.RegisterStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
        }

        Assert.Empty(await StoredJobsNamedAsync(scenario.TenantId, "Ahead job"));
    }

    [Fact]
    public async Task A_platform_owner_impersonating_a_coordinator_starts_a_job_with_that_coordinator_named_and_the_history_showing_the_impersonation()
    {
        var (scenario, coordinator) = await ArrangeAsync("Impersonated Job Staffing");
        Guid job;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var (customer, _) = await ActiveCustomerAsync(keeper, "Impersonated School", "Alva", "Berg");
            job = await RegisterAsync(keeper.Get<JobService>(), "Astronomy", customer, null, scenario.RootId, Occurrence(Weekday.Wednesday, 18, 0, 19, 0));
            await AddSlotAsync(keeper.Get<JobService>().Slots, job, "Lead");
        }

        var platformOwner = scenario.Onboarded.Platform.OwnerId;
        System.Security.Claims.ClaimsPrincipal cookie;
        await using (var endpoint = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var started = await endpoint.Get<ImpersonationService>().StartAsync(SessionCircuit.Cookie(platformOwner, AppRoles.Admin), coordinator, Ct);
            Assert.True(started.IsSuccess, started.Error);
            cookie = started.Value!;
        }

        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
        await using (host)
        {
            await host.Get<UserSessionService>().InitializeFromClaimsAsync();
            Assert.True(runtime.Current.IsImpersonating);
            var start = await StepJobAsync(host.Get<JobService>(), job, JobWorkflowAttribute.StartFromRegistrationStep);
            Assert.True(start.IsSuccess, start.Error);
        }

        Assert.Equal(coordinator, (await StoredJobAsync(job)).CoordinatorUserId);
        await using var context = this.database.CreateSystemContext();
        var entry = await context.StateHistoryEntries.AsNoTracking().SingleAsync(row => row.EntityId == job && row.TransitionKey == JobWorkflowAttribute.StartFromRegistrationStep, Ct);
        Assert.Equal((coordinator, (Guid?)platformOwner), (entry.ActorId, entry.ImpersonatedById));
    }

    [Fact]
    public async Task A_job_has_at_most_fifty_weekly_occurrences_by_the_service_and_the_workflow()
    {
        var (scenario, coordinator) = await ArrangeAsync("Times Job Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var (customer, _) = await ActiveCustomerAsync(host, "Times School", "Bo", "Ahl");

        var full = await jobs.CreateJobAsync(Draft("Full week", customer, null, scenario.RootId, Occurrences(JobRules.MaxOccurrences)), JobWorkflowAttribute.RegisterStep, Ct);
        Assert.True(full.IsSuccess, full.Error);
        Assert.Equal(JobRules.TooManyOccurrences, (await jobs.CreateJobAsync(Draft("Over full", customer, null, scenario.RootId, Occurrences(JobRules.MaxOccurrences + 1)), JobWorkflowAttribute.RegisterStep, Ct)).Error);
        var direct = Draft("Over full", customer, null, scenario.RootId, Occurrences(JobRules.MaxOccurrences + 1));
        direct.TenantId = scenario.TenantId;
        Assert.Equal(JobRules.TooManyOccurrences, (await host.Get<WorkflowService>().CreateAndTransitionAsync<JobState, Job>(direct, JobWorkflowAttribute.RegisterStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);

        var updater = (await jobs.LoadJobUpdaterAsync(full.Value, Ct)).Value!;
        updater.Entity.TimeSlots.Add(new JobTimeSlot { TenantId = scenario.TenantId, JobId = full.Value, Weekday = Weekday.Sunday, Start = new TimeOnly(22, 0), End = new TimeOnly(23, 0) });
        Assert.Equal(JobRules.TooManyOccurrences, (await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveRegistrationStep, null, Ct)).Error);
        Assert.Equal(JobRules.MaxOccurrences, (await StoredJobAsync(full.Value)).TimeSlots.Count);
        Assert.Empty(await StoredJobsNamedAsync(scenario.TenantId, "Over full"));
    }

    [Fact]
    public async Task A_confirmed_job_keeps_its_unit_so_the_units_delete_advises_archiving_and_a_timetable_emptied_while_coordinating_is_not_confirmed()
    {
        var (scenario, coordinator) = await ArrangeAsync("Confirmed Job Staffing");
        Guid unit;
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            unit = await owner.Get<OrganizationService>().CreateUnitIdAsync("Branch North", OrganizationUnitType.Branch, scenario.RootId, Ct);
        }

        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var (customer, _) = await ActiveCustomerAsync(host, "Confirmed School", "Iris", "Holm");
        var job = await RegisterAsync(jobs, "Geology", customer, null, unit, Occurrence(Weekday.Friday, 9, 0, 10, 0));
        await AddSlotAsync(jobs.Slots, job, "Lead");
        Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep)).IsSuccess);

        var emptying = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        emptying.Entity.TimeSlots.Clear();
        Assert.True((await jobs.UpdateJobAndTransitionAsync(emptying, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct)).IsSuccess);
        Assert.Equal(JobWorkflowAttribute.HasOccurrenceReason, (await StepJobAsync(jobs, job, JobWorkflowAttribute.ConfirmStep)).Error);
        Assert.Equal(JobState.Coordinating, (await StoredJobAsync(job)).State);

        var occurrence = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        occurrence.Entity.TimeSlots.Add(new JobTimeSlot { TenantId = scenario.TenantId, JobId = job, Weekday = Weekday.Friday, Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) });
        Assert.True((await jobs.UpdateJobAndTransitionAsync(occurrence, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct)).IsSuccess);
        Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.ConfirmStep)).IsSuccess);
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            var organization = owner.Get<OrganizationService>();
            var unitUpdater = (await organization.LoadOrganizationUnitUpdaterAsync(unit, Ct)).Value!;
            Assert.Equal(JobsBlockUnitDelete.Reason(1, kept: 1), (await organization.UpdateOrganizationUnitAndTransitionAsync(unitUpdater, OrganizationUnitWorkflowAttribute.DeleteStep, null, Ct)).Error);
        }

        Assert.Equal("1 job is run by this unit, which keeps it for its record: archive the unit instead.", JobsBlockUnitDelete.Reason(1, kept: 1));

        Assert.Equal("2 jobs are agreed with this contact, 1 of which keeps it for its record: keep the contact inactive instead.", JobsBlockContactDelete.Reason(2, kept: 1));
        Assert.Equal("3 jobs are agreed with this contact, which keep it for their record: keep the contact inactive instead.", JobsBlockContactDelete.Reason(3, kept: 3));
    }

    private static JobTimeSlot[] Occurrences(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Occurrence((Weekday)(i % 7 + 1), 6 + (i / 7), 0, 7 + (i / 7), 0))];

    private static DetachedEntityUpdater<Job> Edited(Result<DetachedEntityUpdater<Job>> loaded, Guid unit)
    {
        var updater = loaded.Value!;
        updater.Entity.Name = "Statistics renamed";
        updater.Entity.PeriodEnd = updater.Entity.PeriodEnd.AddDays(-7);
        updater.Entity.OrganizationUnitId = unit;
        updater.Entity.TimeSlots.First().End = new TimeOnly(9, 30);
        return updater;
    }

    private static DetachedEntityUpdater<Job> Described(Result<DetachedEntityUpdater<Job>> loaded)
    {
        var updater = loaded.Value!;
        updater.Entity.Description = "Started with its description.";
        updater.Entity.TimeSlots.Add(new JobTimeSlot { TenantId = updater.Entity.TenantId, JobId = updater.Entity.Id, Weekday = Weekday.Thursday, Start = new TimeOnly(14, 0), End = new TimeOnly(15, 0) });
        return updater;
    }

    private static DetachedEntityUpdater<Job> Moved(Result<DetachedEntityUpdater<Job>> loaded)
    {
        var updater = loaded.Value!;
        updater.Entity.PeriodStart = updater.Entity.PeriodStart.AddDays(7);
        updater.Entity.ContactId = null;
        updater.Entity.Contact = null;
        return updater;
    }

    private async Task AssertUnchangedAsync(Guid job, string name, JobState state, Guid contact, Guid unit)
    {
        var stored = await StoredJobAsync(job);
        Assert.Equal((name, state, (Guid?)contact, unit, PeriodStart, PeriodEnd, (string?)null), (stored.Name, stored.State, stored.ContactId, stored.OrganizationUnitId, stored.PeriodStart, stored.PeriodEnd, stored.Description));
        Assert.Equal([(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 0))], stored.TimeSlots.Select(timeSlot => (timeSlot.Weekday, timeSlot.Start, timeSlot.End)));
    }
}
