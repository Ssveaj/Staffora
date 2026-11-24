using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed partial class JobLifecycleTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 1, 11);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 28);

    private readonly RealDatabase database;

    public JobLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Staff_register_a_job_with_a_timetable_and_it_reaches_coordinating_with_two_slots_its_coordinator_set_and_uncovered()
    {
        var (scenario, coordinator) = await ArrangeAsync("Job Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var (customer, contact) = await ActiveCustomerAsync(host, "Brightpath Learning", "Ada", "Moreno");
        var maths = await QualificationAsync(host, "Mathematics");

        var fresh = await jobs.NewJobAsync(Ct);
        Assert.True(fresh.IsSuccess, fresh.Error);
        Assert.Equal((scenario.RootId, JobState.Create), (fresh.Value!.OrganizationUnitId, fresh.Value.State));
        var job = await RegisterAsync(jobs, "  Algebra for adults ", customer, contact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 30), Occurrence(Weekday.Wednesday, 10, 0, 11, 30));
        var stored = await StoredJobAsync(job);
        Assert.Equal(("Algebra for adults", JobState.Registration, scenario.TenantId, customer, contact, scenario.RootId), (stored.Name, stored.State, stored.TenantId, stored.CustomerId, stored.ContactId, stored.OrganizationUnitId));
        Assert.Equal([(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(9, 30)), (Weekday.Wednesday, new TimeOnly(10, 0), new TimeOnly(11, 30))], stored.TimeSlots.OrderBy(timeSlot => timeSlot.Weekday).Select(timeSlot => (timeSlot.Weekday, timeSlot.Start, timeSlot.End)));
        Assert.Null(stored.CoordinatorUserId);
        Assert.Equal((null, null), (stored.CrossTenantId, stored.LinkedCrossTenantId));

        Assert.Contains(JobWorkflowAttribute.HasSlotReason, (await StepJobAsync(jobs, job, JobWorkflowAttribute.HandOverStep)).Error, StringComparison.Ordinal);
        var main = await AddSlotAsync(jobs.Slots, job, "Lead", maths);
        var assistant = await AddSlotAsync(jobs.Slots, job, "Assistant");

        Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.HandOverStep)).IsSuccess);
        Assert.Null((await StoredJobAsync(job)).CoordinatorUserId);
        Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.StartStep)).IsSuccess);
        stored = await StoredJobAsync(job);
        Assert.Equal((JobState.Coordinating, coordinator), (stored.State, stored.CoordinatorUserId));
        Assert.Equal(["job-register", "job-hand-over", "job-start"], await HistoryKeysAsync(nameof(Job), job));
        Assert.Equal([JobSlotState.Active, JobSlotState.Active], (await StoredSlotsAsync(job)).Select(slot => slot.State));
        Assert.Equal([maths], (await StoredSlotsAsync(job)).Single(slot => slot.Id == main).Qualifications.Select(required => required.QualificationId));

        var page = await jobs.QueryJobsAsync(new ListQuery().WithFilter(JobList.Status, "coordinating"), Ct);
        Assert.True(page.IsSuccess, page.Error);
        var row = Assert.Single(page.Value!.Rows);
        Assert.Equal((JobCoverage.Uncovered, 2, "Uncovered (2 slots)", "Brightpath Learning", "Casey Example"), (row.Coverage, row.ActiveSlots, row.CoverageLabel, row.Customer, row.Coordinator));
        var options = await jobs.GetJobEditorOptionsAsync(job, Ct);
        Assert.All(options.Value!.Staff!, entry => Assert.Equal((0, 1, 0), (entry.Assigned.Count, entry.Open, entry.Asked.Count)));
        Assert.Equal(2, options.Value.Staff!.Count);
        var slots = await jobs.QueryJobSlotsAsync(job, new ListQuery(), Ct);
        Assert.Equal([("Assistant", "Uncovered", "None"), ("Lead", "Uncovered", "Mathematics")], slots.Value!.Rows.Select(slot => (slot.Name, slot.CoverageLabel, slot.QualificationsLabel)));
        Assert.Contains(assistant, slots.Value.Rows.Select(slot => slot.Id));

        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        updater.Entity.CoordinatorUserId = scenario.Onboarded.OwnerId;
        Assert.False((await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct)).IsSuccess);
        Assert.Equal(coordinator, (await StoredJobAsync(job)).CoordinatorUserId);
    }

    [Fact]
    public async Task Deleting_a_customer_contact_or_unit_the_job_uses_is_refused_with_the_reason()
    {
        var (scenario, coordinator) = await ArrangeAsync("Blocking Job Staffing");
        Guid unit;
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            unit = await owner.Get<OrganizationService>().CreateUnitIdAsync("Branch East", OrganizationUnitType.Branch, scenario.RootId, Ct);
        }

        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var customers = host.Get<CustomerService>();
        var (customer, contact) = await ActiveCustomerAsync(host, "Harbor College", "Cleo", "Hart");
        var job = await RegisterAsync(host.Get<JobService>(), "Physics", customer, contact, unit, Occurrence(Weekday.Tuesday, 13, 0, 15, 0));

        Assert.True((await StepContactAsync(customers.Contacts, contact, CustomerContactWorkflowAttribute.DeactivateStep)).IsSuccess);
        Assert.Equal(JobsBlockContactDelete.Reason(1), (await StepContactAsync(customers.Contacts, contact, CustomerContactWorkflowAttribute.DeleteStep)).Error);

        Assert.True((await StepCustomerAsync(customers, customer, CustomerWorkflowAttribute.DeactivateStep)).IsSuccess);
        var refused = await StepCustomerAsync(customers, customer, CustomerWorkflowAttribute.DeleteStep);
        Assert.Contains(JobsBlockCustomerDelete.Reason(1), refused.Error, StringComparison.Ordinal);
        var footer = await host.Get<WorkflowService>().GetAvailableTransitionsAsync<CustomerState, Customer>(customer, CustomerWorkflowQueries.WithWorkflowGraph, Ct);
        Assert.Contains(JobsBlockCustomerDelete.Reason(1), footer.Value!.Single(step => step.Transition.Key == CustomerWorkflowAttribute.DeleteStep).BlockingReasons);

        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            var organization = owner.Get<OrganizationService>();
            var unitUpdater = (await organization.LoadOrganizationUnitUpdaterAsync(unit, Ct)).Value!;
            Assert.Equal(JobsBlockUnitDelete.Reason(1), (await organization.UpdateOrganizationUnitAndTransitionAsync(unitUpdater, OrganizationUnitWorkflowAttribute.DeleteStep, null, Ct)).Error);
        }

        await using var context = this.database.CreateSystemContext();
        Assert.Equal((CustomerState.Inactive, false), await context.Customers.Where(row => row.Id == customer).Select(row => ValueTuple.Create(row.State, row.IsInTransition)).SingleAsync(Ct));
        Assert.Equal((CustomerContactState.Inactive, false), await context.CustomerContacts.Where(row => row.Id == contact).Select(row => ValueTuple.Create(row.State, row.IsInTransition)).SingleAsync(Ct));
        Assert.Equal((OrganizationUnitState.Active, false), await context.OrganizationUnits.Where(row => row.Id == unit).Select(row => ValueTuple.Create(row.State, row.IsInTransition)).SingleAsync(Ct));
        Assert.Equal((customer, contact, unit), await context.Jobs.Where(row => row.Id == job).Select(row => ValueTuple.Create(row.CustomerId, row.ContactId, row.OrganizationUnitId)).SingleAsync(Ct));
    }

    [Fact]
    public async Task The_period_and_the_timetable_are_judged_with_each_problem_in_words_by_the_service_and_the_workflow()
    {
        var (scenario, coordinator) = await ArrangeAsync("Timetable Job Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var (customer, _) = await ActiveCustomerAsync(host, "Meadow School", "Hal", "Berg");

        var overlapping = Draft("Chemistry", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 10, 0), Occurrence(Weekday.Monday, 9, 0, 11, 0));
        Assert.Equal("Monday: 08:00-10:00 and 09:00-11:00 overlap.", (await jobs.CreateJobAsync(overlapping, JobWorkflowAttribute.RegisterStep, Ct)).Error);
        var backwards = Draft("Chemistry", customer, null, scenario.RootId, Occurrence(Weekday.Friday, 12, 0, 11, 0));
        Assert.Equal("Friday: a time (12:00-11:00) must start before it ends.", (await jobs.CreateJobAsync(backwards, JobWorkflowAttribute.RegisterStep, Ct)).Error);
        var reversed = Draft("Chemistry", customer, null, scenario.RootId);
        (reversed.PeriodStart, reversed.PeriodEnd) = (PeriodEnd, PeriodStart);
        Assert.Equal(JobRules.PeriodEndsBeforeStart, (await jobs.CreateJobAsync(reversed, JobWorkflowAttribute.RegisterStep, Ct)).Error);

        var direct = Draft("Chemistry", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 10, 0), Occurrence(Weekday.Monday, 9, 30, 11, 0));
        direct.TenantId = scenario.TenantId;
        var past = await host.Get<WorkflowService>().CreateAndTransitionAsync<JobState, Job>(direct, JobWorkflowAttribute.RegisterStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.Equal("Monday: 08:00-10:00 and 09:30-11:00 overlap.", past.Error);

        var job = await RegisterAsync(jobs, "Chemistry", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0), Occurrence(Weekday.Monday, 9, 0, 10, 0));
        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        updater.Entity.TimeSlots.Single(timeSlot => timeSlot.Start == new TimeOnly(9, 0)).Start = new TimeOnly(8, 30);
        Assert.Equal("Monday: 08:00-09:00 and 08:30-10:00 overlap.", (await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveRegistrationStep, null, Ct)).Error);
        Assert.Equal(2, (await StoredJobAsync(job)).TimeSlots.Count);
        Assert.Empty(await StoredJobsNamedAsync(scenario.TenantId, "Chemistry", except: job));
    }

    [Fact]
    public async Task A_contact_is_one_of_the_jobs_customers_by_every_path()
    {
        var (scenario, coordinator) = await ArrangeAsync("Contact Job Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var workflows = host.Get<WorkflowService>();
        var (customer, contact) = await ActiveCustomerAsync(host, "Willow School", "Joy", "Park");
        var (_, otherContact) = await ActiveCustomerAsync(host, "Oak Academy", "Kim", "Lund");

        Assert.Contains(JobWorkflowAttribute.ContactOfCustomerReason, (await jobs.CreateJobAsync(Draft("Biology", customer, otherContact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0)), JobWorkflowAttribute.RegisterStep, Ct)).Error, StringComparison.Ordinal);
        var engineDraft = Draft("Biology", customer, otherContact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
        engineDraft.TenantId = scenario.TenantId;
        Assert.Equal(JobWorkflowAttribute.ContactOfCustomerReason, (await workflows.CreateAndTransitionAsync<JobState, Job>(engineDraft, JobWorkflowAttribute.RegisterStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);

        var forged = Draft("Biology", customer, otherContact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
        forged.TenantId = scenario.TenantId;
        forged.Contact = new CustomerContact { Id = otherContact, CustomerId = customer, State = CustomerContactState.Active, FirstName = "Fake", LastName = "Contact" };
        Assert.Equal(JobWorkflowAttribute.ContactOfCustomerReason, (await workflows.CreateAndTransitionAsync<JobState, Job>(forged, JobWorkflowAttribute.RegisterStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
        Assert.Empty(await StoredJobsNamedAsync(scenario.TenantId, "Biology"));

        var job = await RegisterAsync(jobs, "Biology", customer, contact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        updater.Entity.ContactId = otherContact;
        Assert.Contains(JobWorkflowAttribute.ContactOfCustomerReason, (await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveRegistrationStep, null, Ct)).Error, StringComparison.Ordinal);
        var past = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        past.Entity.ContactId = otherContact;
        past.Entity.Contact = new CustomerContact { Id = otherContact, CustomerId = customer, State = CustomerContactState.Active, FirstName = "Fake", LastName = "Contact" };
        Assert.False((await workflows.SaveAndTransitionAsync<JobState, Job>(past, JobWorkflowAttribute.SaveRegistrationStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        Assert.Equal(contact, (await StoredJobAsync(job)).ContactId);
    }

    [Fact]
    public async Task Only_a_customer_contact_and_unit_in_use_are_chosen_and_one_kept_as_stored_stays_as_it_is()
    {
        var (scenario, coordinator) = await ArrangeAsync("Choosing Job Staffing");
        Guid unit;
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            var organization = owner.Get<OrganizationService>();
            unit = await organization.CreateUnitIdAsync("Branch West", OrganizationUnitType.Branch, scenario.RootId, Ct);
            var unitUpdater = (await organization.LoadOrganizationUnitUpdaterAsync(unit, Ct)).Value!;
            Assert.True((await organization.UpdateOrganizationUnitAndTransitionAsync(unitUpdater, OrganizationUnitWorkflowAttribute.DeactivateStep, null, Ct)).IsSuccess);
        }

        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var customers = host.Get<CustomerService>();
        var (customer, contact) = await ActiveCustomerAsync(host, "Lakeside School", "Ivy", "Nash");
        var (quiet, _) = await ActiveCustomerAsync(host, "Quiet College", "Lea", "Moss");
        Assert.True((await StepContactAsync(customers.Contacts, contact, CustomerContactWorkflowAttribute.DeactivateStep)).IsSuccess);
        Assert.True((await StepCustomerAsync(customers, quiet, CustomerWorkflowAttribute.DeactivateStep)).IsSuccess);

        Assert.Contains(JobWorkflowAttribute.ChosenCustomerReason, (await jobs.CreateJobAsync(Draft("History", quiet, null, scenario.RootId), JobWorkflowAttribute.RegisterStep, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(JobWorkflowAttribute.ChosenContactReason, (await jobs.CreateJobAsync(Draft("History", customer, contact, scenario.RootId), JobWorkflowAttribute.RegisterStep, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(JobWorkflowAttribute.ChosenUnitReason, (await jobs.CreateJobAsync(Draft("History", customer, null, unit), JobWorkflowAttribute.RegisterStep, Ct)).Error, StringComparison.Ordinal);

        var other = (await ActiveCustomerAsync(host, "Steady School", "Max", "Ray")).Customer;
        var job = await RegisterAsync(jobs, "History", other, null, scenario.RootId, Occurrence(Weekday.Thursday, 8, 0, 9, 0));
        await AddSlotAsync(jobs.Slots, job, "Lead");
        Assert.True((await StepCustomerAsync(customers, other, CustomerWorkflowAttribute.DeactivateStep)).IsSuccess);
        var kept = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        kept.Entity.Description = "Kept for its customer.";
        Assert.True((await jobs.UpdateJobAndTransitionAsync(kept, JobWorkflowAttribute.SaveRegistrationStep, null, Ct)).IsSuccess);
        Assert.Contains(JobWorkflowAttribute.CustomerActiveReason, (await StepJobAsync(jobs, job, JobWorkflowAttribute.HandOverStep)).Error, StringComparison.Ordinal);
        Assert.Contains(JobWorkflowAttribute.CustomerActiveReason, (await StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep)).Error, StringComparison.Ordinal);
        var moved = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        moved.Entity.CustomerId = quiet;
        Assert.Contains(JobWorkflowAttribute.ChosenCustomerReason, (await jobs.UpdateJobAndTransitionAsync(moved, JobWorkflowAttribute.SaveRegistrationStep, null, Ct)).Error, StringComparison.Ordinal);
        Assert.Equal(JobState.Registration, (await StoredJobAsync(job)).State);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_registration_waits_for_a_customers_deactivation_and_is_judged_after_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Racing Job Staffing");
        Guid customer;
        await using (var setup = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            customer = (await ActiveCustomerAsync(setup, "Racing School", "Ola", "Berg")).Customer;
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var deactivating = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator), services => services.AddSingleton<IStepPreparation>(new PauseOnce(typeof(Customer), entered, gate)));
        var deactivation = StepCustomerAsync(deactivating.Get<CustomerService>(), customer, CustomerWorkflowAttribute.DeactivateStep);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        await using var registering = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var registration = registering.Get<JobService>().CreateJobAsync(Draft("Racing job", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0)), JobWorkflowAttribute.RegisterStep, cancellationToken);
        await WaitForApplicationLockWaitAsync(registration);
        Assert.False(registration.IsCompleted, "The registration did not wait for the customer lock the deactivation holds.");

        gate.SetResult();
        Assert.True((await deactivation).IsSuccess);
        var registered = await registration;
        Assert.False(registered.IsSuccess);
        Assert.Contains(JobWorkflowAttribute.ChosenCustomerReason, registered.Error, StringComparison.Ordinal);
        Assert.Empty(await StoredJobsNamedAsync(scenario.TenantId, "Racing job"));
    }

    [Fact]
    public async Task Every_job_step_takes_three_application_locks_and_every_slot_step_one()
    {
        var (scenario, coordinator) = await ArrangeAsync("Locking Job Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var (customer, _) = await ActiveCustomerAsync(host, "Locking School", "Pia", "Holm");

        host.ClearCommandLog();
        var job = await RegisterAsync(jobs, "Locked", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
        Assert.Equal(3, host.CommandLog.Count(command => command.Contains("sp_getapplock", StringComparison.Ordinal)));
        host.ClearCommandLog();
        var slot = await AddSlotAsync(jobs.Slots, job, "Lead");
        Assert.Equal(1, host.CommandLog.Count(command => command.Contains("sp_getapplock", StringComparison.Ordinal)));
        host.ClearCommandLog();
        Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep)).IsSuccess);
        Assert.Equal(3, host.CommandLog.Count(command => command.Contains("sp_getapplock", StringComparison.Ordinal)));
        Assert.StartsWith("jobs:", AppLock.Jobs.Resource(scenario.TenantId), StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, slot);
    }

    [Fact]
    public async Task Slots_change_only_while_the_job_takes_slots_and_a_qualification_required_anew_is_active()
    {
        var (scenario, coordinator) = await ArrangeAsync("Slots Job Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var jobs = host.Get<JobService>();
        var (customer, _) = await ActiveCustomerAsync(host, "Slots School", "Rob", "Lind");
        var art = await QualificationAsync(host, "Art");
        var music = await QualificationAsync(host, "Music");
        var job = await RegisterAsync(jobs, "Painting", customer, null, scenario.RootId, Occurrence(Weekday.Friday, 14, 0, 16, 0));
        var main = await AddSlotAsync(jobs.Slots, job, "Lead", art);
        var spare = await AddSlotAsync(jobs.Slots, job, "Spare");

        var withEdits = (await jobs.Slots.LoadJobSlotUpdaterAsync(spare, Ct)).Value!;
        withEdits.Entity.Name = "Renamed";
        Assert.Equal(TransitionRefusals.PendingEdits, (await jobs.Slots.UpdateJobSlotAndTransitionAsync(withEdits, JobSlotWorkflowAttribute.CancelStep, null, Ct)).Error);
        Assert.True((await StepSlotAsync(jobs.Slots, spare, JobSlotWorkflowAttribute.CancelStep)).IsSuccess);
        Assert.Equal(JobSlotState.Canceled, (await StoredSlotsAsync(job)).Single(slot => slot.Id == spare).State);

        await ArchiveQualificationAsync(host, music);
        Assert.Equal(JobSlotService.QualificationNotActive, (await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Singer", Qualifications = [new JobSlotQualification { QualificationId = music }] }, JobSlotWorkflowAttribute.AddStep, Ct)).Error);
        var adding = (await jobs.Slots.LoadJobSlotUpdaterAsync(main, Ct)).Value!;
        adding.Entity.Qualifications.Add(new JobSlotQualification { TenantId = scenario.TenantId, JobSlotId = main, QualificationId = music });
        Assert.Equal(JobSlotService.QualificationNotActive, (await jobs.Slots.UpdateJobSlotAndTransitionAsync(adding, JobSlotWorkflowAttribute.SaveStep, null, Ct)).Error);

        Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep)).IsSuccess);
        Assert.True((await StepJobAsync(jobs, job, JobWorkflowAttribute.ConfirmStep)).IsSuccess);
        Assert.Contains(JobSlotWorkflowAttribute.JobTakesSlotsReason, (await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Late" }, JobSlotWorkflowAttribute.AddStep, Ct)).Error, StringComparison.Ordinal);
        var renaming = (await jobs.Slots.LoadJobSlotUpdaterAsync(main, Ct)).Value!;
        renaming.Entity.Name = "Lead staff member";
        Assert.Contains(JobSlotWorkflowAttribute.JobTakesSlotsReason, (await jobs.Slots.UpdateJobSlotAndTransitionAsync(renaming, JobSlotWorkflowAttribute.SaveStep, null, Ct)).Error, StringComparison.Ordinal);
        Assert.Contains(JobSlotWorkflowAttribute.JobTakesSlotsReason, (await StepSlotAsync(jobs.Slots, main, JobSlotWorkflowAttribute.CancelStep)).Error, StringComparison.Ordinal);
        var stored = (await StoredSlotsAsync(job)).Single(slot => slot.Id == main);
        Assert.Equal(("Lead", JobSlotState.Active), (stored.Name, stored.State));
        Assert.Equal([art], stored.Qualifications.Select(required => required.QualificationId));
    }

    [Fact]
    public async Task A_reader_of_jobs_reads_them_and_changes_nothing_and_anyone_else_reads_nothing()
    {
        var (scenario, coordinator) = await ArrangeAsync("Reading Job Staffing");
        Guid job;
        Guid slot;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var (customer, _) = await ActiveCustomerAsync(keeper, "Reading School", "Sam", "Eld");
            job = await RegisterAsync(keeper.Get<JobService>(), "Literature", customer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            slot = await AddSlotAsync(keeper.Get<JobService>().Slots, job, "Lead");
        }

        var reader = await scenario.AddPersonAsync(this.database, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, Ct, RolePermission.JobsView);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(reader)))
        {
            var jobs = host.Get<JobService>();
            Assert.Equal(["Literature"], (await jobs.QueryJobsAsync(new ListQuery(), Ct)).Value!.Rows.Select(row => row.Name));
            Assert.Equal(["Lead"], (await jobs.QueryJobSlotsAsync(job, new ListQuery(), Ct)).Value!.Rows.Select(row => row.Name));
            Assert.True((await jobs.GetJobEditorOptionsAsync(job, Ct)).Value!.FieldsLocked);
            Assert.False((await jobs.GetJobEditorOptionsAsync(job, Ct)).Value!.MayAddSlots);
            Assert.True((await jobs.Slots.GetJobSlotEditorOptionsAsync(slot, Ct)).Value!.FieldsLocked);
            Assert.Equal(JobService.NoCreate, (await jobs.NewJobAsync(Ct)).Error);
            Assert.Equal(JobService.NoCreate, (await jobs.CreateJobAsync(new Job { Name = "Other" }, JobWorkflowAttribute.RegisterStep, Ct)).Error);
            Assert.Equal(JobService.NoChange, (await StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep)).Error);
            Assert.Equal(JobSlotService.NoEdit, (await StepSlotAsync(jobs.Slots, slot, JobSlotWorkflowAttribute.CancelStep)).Error);
            Assert.Equal(JobSlotService.NoEdit, (await jobs.Slots.NewJobSlotAsync(job, Ct)).Error);

            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<JobState, Job>(job, JobWorkflowAttribute.StartFromRegistrationStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<JobSlotState, JobSlot>(slot, JobSlotWorkflowAttribute.CancelStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        }

        Assert.Equal(JobState.Registration, (await StoredJobAsync(job)).State);
        Assert.Equal(JobSlotState.Active, (await StoredSlotsAsync(job)).Single().State);

        var stranger = await scenario.AddPersonAsync(this.database, "Stranger", Ct);
        await using var strangerHost = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(stranger));
        var none = strangerHost.Get<JobService>();
        Assert.Equal(JobService.NoView, (await none.QueryJobsAsync(new ListQuery(), Ct)).Error);
        Assert.Equal(JobService.NoView, (await none.QueryJobSlotsAsync(job, new ListQuery(), Ct)).Error);
        Assert.Equal(JobService.NoView, (await none.LoadJobUpdaterAsync(job, Ct)).Error);
        Assert.Equal(JobService.NoView, (await none.Slots.LoadJobSlotUpdaterAsync(slot, Ct)).Error);
        Assert.Equal(JobService.NoView, (await none.GetJobHistoryAsync(job, new StateHistoryQuery(10), Ct)).Error);
    }

    [Fact]
    public async Task Another_tenant_and_the_platform_console_reach_no_job_or_slot_and_name_no_row_of_the_tenant()
    {
        var (scenario, coordinator) = await ArrangeAsync("Isolated Job Staffing");
        Guid job;
        Guid slot;
        Guid customer;
        Guid contact;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            (customer, contact) = await ActiveCustomerAsync(keeper, "Isolated School", "Tove", "Ek");
            job = await RegisterAsync(keeper.Get<JobService>(), "Geography", customer, contact, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            slot = await AddSlotAsync(keeper.Get<JobService>().Slots, job, "Lead");
        }

        var other = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName("Other Isolated Job Staffing"));
        await using (var host = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime()))
        {
            var jobs = host.Get<JobService>();
            var (ownCustomer, _) = await ActiveCustomerAsync(host, "Own school", "Uma", "Ny");
            await RegisterAsync(jobs, "Own job", ownCustomer, null, other.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0));
            Assert.Equal(["Own job"], (await jobs.QueryJobsAsync(new ListQuery(), Ct)).Value!.Rows.Select(row => row.Name));
            Assert.Empty((await jobs.QueryJobSlotsAsync(job, new ListQuery(), Ct)).Value!.Rows);
            Assert.Equal(JobService.NotFound, (await jobs.LoadJobUpdaterAsync(job, Ct)).Error);
            Assert.Equal(JobSlotService.NotFound, (await jobs.Slots.LoadJobSlotUpdaterAsync(slot, Ct)).Error);
            Assert.Equal(JobService.NotFound, (await jobs.Slots.NewJobSlotAsync(job, Ct)).Error);
            Assert.Equal(JobService.NotFound, (await jobs.GetJobEditorOptionsAsync(job, Ct)).Error);
            Assert.False((await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Spy" }, JobSlotWorkflowAttribute.AddStep, Ct)).IsSuccess);
            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<JobState, Job>(job, JobWorkflowAttribute.StartFromRegistrationStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<JobSlotState, JobSlot>(slot, JobSlotWorkflowAttribute.CancelStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);

            var unknown = Guid.NewGuid();
            var namingNothing = (await jobs.CreateJobAsync(Draft("Spy job", unknown, null, other.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0)), JobWorkflowAttribute.RegisterStep, Ct)).Error;
            Assert.Equal(namingNothing, (await jobs.CreateJobAsync(Draft("Spy job", customer, null, other.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0)), JobWorkflowAttribute.RegisterStep, Ct)).Error);
            Assert.False((await jobs.CreateJobAsync(Draft("Spy job", ownCustomer, contact, other.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0)), JobWorkflowAttribute.RegisterStep, Ct)).IsSuccess);
            Assert.False((await jobs.CreateJobAsync(Draft("Spy job", ownCustomer, null, scenario.RootId, Occurrence(Weekday.Monday, 8, 0, 9, 0)), JobWorkflowAttribute.RegisterStep, Ct)).IsSuccess);
            Assert.Empty(await StoredJobsNamedAsync(other.TenantId, "Spy job"));
        }

        await using (var console = ServiceHost.Create(this.database, scenario.Onboarded.Platform.PlatformOwnerRuntime()))
        {
            var jobs = console.Get<JobService>();
            Assert.Empty((await jobs.QueryJobsAsync(new ListQuery(), Ct)).Value!.Rows);
            Assert.Equal(JobService.ChangeInOwnTenantOnly, (await jobs.NewJobAsync(Ct)).Error);
            Assert.Equal(JobService.ChangeInOwnTenantOnly, (await jobs.CreateJobAsync(Draft("Console job", customer, null, scenario.RootId), JobWorkflowAttribute.RegisterStep, Ct)).Error);
            Assert.Equal(JobService.ChangeInOwnTenantOnly, (await jobs.Slots.NewJobSlotAsync(job, Ct)).Error);
            var reached = await console.Get<WorkflowService>().TransitionEntityAsync<JobState, Job>(job, JobWorkflowAttribute.StartFromRegistrationStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Contains(JobWorkflowAttribute.OwnTenantReason, reached.Error, StringComparison.Ordinal);
            var reachedSlot = await console.Get<WorkflowService>().TransitionEntityAsync<JobSlotState, JobSlot>(slot, JobSlotWorkflowAttribute.CancelStep, JobSlotWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Contains(JobWorkflowAttribute.OwnTenantReason, reachedSlot.Error, StringComparison.Ordinal);
        }

        Assert.Equal(JobState.Registration, (await StoredJobAsync(job)).State);
        Assert.Equal(JobSlotState.Active, (await StoredSlotsAsync(job)).Single().State);
    }

    private async Task<(StaffScenario Scenario, Guid Coordinator)> ArrangeAsync(string name)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName(name));
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        return (scenario, coordinator);
    }

    private async Task<(Guid Customer, Guid Contact)> ActiveCustomerAsync(ServiceHost host, string name, string firstName, string lastName)
    {
        var customers = host.Get<CustomerService>();
        var customer = await customers.CreateCustomerAsync(new Customer { Name = name }, CustomerWorkflowAttribute.ActivateStep, Ct);
        Assert.True(customer.IsSuccess, customer.Error);
        var contact = await customers.Contacts.CreateCustomerContactAsync(new CustomerContact { CustomerId = customer.Value, FirstName = firstName, LastName = lastName }, CustomerContactWorkflowAttribute.ActivateStep, Ct);
        Assert.True(contact.IsSuccess, contact.Error);
        return (customer.Value, contact.Value);
    }

    private async Task<Guid> QualificationAsync(ServiceHost host, string name)
    {
        var created = await host.Get<QualificationService>().CreateQualificationAsync(new Qualification { Name = name }, QualificationWorkflowAttribute.CreateStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task ArchiveQualificationAsync(ServiceHost host, Guid qualification)
    {
        var qualifications = host.Get<QualificationService>();
        var updater = (await qualifications.LoadQualificationUpdaterAsync(qualification, Ct)).Value!;
        Assert.True((await qualifications.UpdateQualificationAndTransitionAsync(updater, QualificationWorkflowAttribute.ArchiveStep, null, Ct)).IsSuccess);
    }

    private static JobTimeSlot Occurrence(Weekday day, int startHour, int startMinute, int endHour, int endMinute) =>
        new() { Weekday = day, Start = new TimeOnly(startHour, startMinute), End = new TimeOnly(endHour, endMinute) };

    private static Job Draft(string name, Guid customer, Guid? contact, Guid unit, params JobTimeSlot[] occurrences) =>
        new() { Name = name, CustomerId = customer, ContactId = contact, OrganizationUnitId = unit, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd, TimeSlots = [.. occurrences] };

    private async Task<Guid> RegisterAsync(JobService jobs, string name, Guid customer, Guid? contact, Guid unit, params JobTimeSlot[] occurrences)
    {
        var created = await jobs.CreateJobAsync(Draft(name, customer, contact, unit, occurrences), JobWorkflowAttribute.RegisterStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Guid> AddSlotAsync(JobSlotService slots, Guid job, string name, params Guid[] qualifications)
    {
        var added = await slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = name, Qualifications = [.. qualifications.Select(id => new JobSlotQualification { QualificationId = id })] }, JobSlotWorkflowAttribute.AddStep, Ct);
        Assert.True(added.IsSuccess, added.Error);
        return added.Value;
    }

    private async Task<Result> StepJobAsync(JobService jobs, Guid job, string step)
    {
        var updater = await jobs.LoadJobUpdaterAsync(job, Ct);
        return updater.IsSuccess ? await jobs.UpdateJobAndTransitionAsync(updater.Value!, step, null, Ct) : Result.Failure(updater.Error);
    }

    private async Task<Result> StepSlotAsync(JobSlotService slots, Guid slot, string step)
    {
        var updater = await slots.LoadJobSlotUpdaterAsync(slot, Ct);
        return updater.IsSuccess ? await slots.UpdateJobSlotAndTransitionAsync(updater.Value!, step, null, Ct) : Result.Failure(updater.Error);
    }

    private async Task<Result> StepCustomerAsync(CustomerService customers, Guid customer, string step)
    {
        var updater = await customers.LoadCustomerUpdaterAsync(customer, Ct);
        return updater.IsSuccess ? await customers.UpdateCustomerAndTransitionAsync(updater.Value!, step, null, Ct) : Result.Failure(updater.Error);
    }

    private async Task<Result> StepContactAsync(CustomerContactService contacts, Guid contact, string step)
    {
        var updater = await contacts.LoadCustomerContactUpdaterAsync(contact, Ct);
        return updater.IsSuccess ? await contacts.UpdateCustomerContactAndTransitionAsync(updater.Value!, step, null, Ct) : Result.Failure(updater.Error);
    }

    private async Task<Job> StoredJobAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Jobs.AsNoTracking().Include(job => job.TimeSlots).SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<List<Job>> StoredJobsNamedAsync(Guid tenantId, string name, Guid? except = null)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Jobs.AsNoTracking().Where(row => row.TenantId == tenantId && row.Name == name && row.Id != except).ToListAsync(Ct);
    }

    private async Task<List<JobSlot>> StoredSlotsAsync(Guid job)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.JobSlots.AsNoTracking().Include(slot => slot.Qualifications).Where(row => row.JobId == job).OrderBy(row => row.Name).ToListAsync(Ct);
    }

    private async Task<List<string>> HistoryKeysAsync(string entityType, Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityType == entityType && entry.EntityId == id).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).Select(entry => entry.TransitionKey).ToListAsync(Ct);
    }

    private async Task WaitForApplicationLockWaitAsync(Task call)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!call.IsCompleted && !timeout.IsCancellationRequested)
        {
            await using var context = this.database.CreateSystemContext();
            var waiting = await context.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND resource_database_id = DB_ID()")
                .SingleAsync(Ct);
            if (waiting > 0)
            {
                return;
            }

            await Task.Yield();
        }
    }

    private sealed class PauseOnce : IStepPreparation
    {
        private readonly Type entityType;
        private readonly TaskCompletionSource entered;
        private readonly TaskCompletionSource gate;

        public PauseOnce(Type entityType, TaskCompletionSource entered, TaskCompletionSource gate)
        {
            this.entityType = entityType;
            this.entered = entered;
            this.gate = gate;
        }

        private int paused;

        public Type EntityType => this.entityType;

        public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this.paused, 1) == 0)
            {
                this.entered.TrySetResult();
                await this.gate.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
