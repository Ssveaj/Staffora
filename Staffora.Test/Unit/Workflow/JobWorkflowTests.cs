using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class JobWorkflowTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private static Workflow<JobState, Job> Jobs => WorkflowRegistry.Default.Get<JobState, Job>();

    private static Workflow<JobSlotState, JobSlot> Slots => WorkflowRegistry.Default.Get<JobSlotState, JobSlot>();

    [Fact]
    public void The_lifecycles_have_the_planned_states_with_their_stored_values_and_the_steps_of_this_step()
    {
        Assert.Equal(
            [("Create", 0), ("Registration", 1), ("AwaitingCoordination", 2), ("Coordinating", 3), ("Confirmed", 4), ("InProgress", 5), ("Completed", 6), ("CanceledByCustomer", 7), ("CanceledBySupplier", 8), ("Deleted", 9)],
            Enum.GetValues<JobState>().Select(state => (state.ToString(), (int)state)));
        Assert.Equal([("Active", 0), ("Canceled", 1), ("Deleted", 2), ("Create", -1)], Enum.GetValues<JobSlotState>().Select(state => (state.ToString(), (int)state)));
        Assert.Equal(
            [
                (JobWorkflowAttribute.RegisterStep, JobState.Create, JobState.Registration, RolePermission.JobsCreate),
                (JobWorkflowAttribute.SaveRegistrationStep, JobState.Registration, JobState.Registration, RolePermission.JobsEdit),
                (JobWorkflowAttribute.HandOverStep, JobState.Registration, JobState.AwaitingCoordination, RolePermission.JobsCreate),
                (JobWorkflowAttribute.SaveAwaitingStep, JobState.AwaitingCoordination, JobState.AwaitingCoordination, RolePermission.JobsEdit),
                (JobWorkflowAttribute.BackToRegistrationStep, JobState.AwaitingCoordination, JobState.Registration, RolePermission.JobsEdit),
                (JobWorkflowAttribute.StartStep, JobState.AwaitingCoordination, JobState.Coordinating, RolePermission.PerformCoordination),
                (JobWorkflowAttribute.StartFromRegistrationStep, JobState.Registration, JobState.Coordinating, RolePermission.PerformCoordination),
                (JobWorkflowAttribute.SaveCoordinatingStep, JobState.Coordinating, JobState.Coordinating, RolePermission.JobsEdit),
                (JobWorkflowAttribute.ConfirmStep, JobState.Coordinating, JobState.Confirmed, RolePermission.PerformCoordination),
                (JobWorkflowAttribute.SaveConfirmedStep, JobState.Confirmed, JobState.Confirmed, RolePermission.JobsEdit),
                (JobWorkflowAttribute.SaveInProgressStep, JobState.InProgress, JobState.InProgress, RolePermission.JobsEdit),
                (JobWorkflowAttribute.DeleteStep, JobState.Registration, JobState.Deleted, RolePermission.JobsDelete),
                .. JobWorkflowAttribute.Cancelable.SelectMany(cancelable => new[]
                {
                    (JobWorkflowAttribute.CancelStepFrom(cancelable.State), cancelable.State, JobState.CanceledBySupplier, RolePermission.JobsCancel),
                    (JobWorkflowAttribute.CustomerCancelStepFrom(cancelable.State), cancelable.State, JobState.CanceledByCustomer, RolePermission.JobsCancel),
                }),
            ],
            Jobs.Transitions.Where(transition => !transition.IsSystemDriven).Select(transition => (transition.Key, transition.From, transition.To, transition.RequiredPermission!.Value)));

        Assert.Equal(
            [
                (JobWorkflowAttribute.PeriodStartsStep, JobState.Confirmed, JobState.InProgress), (JobWorkflowAttribute.PeriodEndsStep, JobState.InProgress, JobState.Completed), (JobWorkflowAttribute.ReceiveStep, JobState.Create, JobState.AwaitingCoordination),
                .. JobWorkflowAttribute.Cancelable.Select(cancelable => (JobWorkflowAttribute.CanceledInPortalStepFrom(cancelable.State), cancelable.State, JobState.CanceledByCustomer)),
            ],
            Jobs.Transitions.Where(transition => transition.IsSystemDriven).Select(transition => (transition.Key, transition.From, transition.To)));
        Assert.All(Jobs.Transitions.Where(transition => transition.IsSystemDriven), transition => Assert.Equal((null, false, false, transition.To == JobState.CanceledByCustomer), (transition.RequiredPermission, transition.RequiresPlatformOwner, transition.IsSave, transition.TakesNoEdits)));
        Assert.Equal(
            [(JobSlotWorkflowAttribute.AddStep, JobSlotState.Create, JobSlotState.Active), (JobSlotWorkflowAttribute.SaveStep, JobSlotState.Active, JobSlotState.Active), (JobSlotWorkflowAttribute.CancelStep, JobSlotState.Active, JobSlotState.Canceled)],
            Slots.Transitions.Where(transition => !transition.IsSystemDriven).Select(transition => (transition.Key, transition.From, transition.To)));
        Assert.All(Slots.Transitions.Where(transition => !transition.IsSystemDriven), transition => Assert.Equal(RolePermission.JobsEdit, transition.RequiredPermission));

        Assert.Equal(
            [(JobSlotWorkflowAttribute.ReceiveStep, JobSlotState.Create, JobSlotState.Active, (RolePermission?)null), (JobSlotWorkflowAttribute.DeleteWithJobStep, JobSlotState.Active, JobSlotState.Deleted, null), (JobSlotWorkflowAttribute.DeleteCanceledWithJobStep, JobSlotState.Canceled, JobSlotState.Deleted, null)],
            Slots.Transitions.Where(transition => transition.IsSystemDriven).Select(transition => (transition.Key, transition.From, transition.To, transition.RequiredPermission)));
        Assert.True(Slots.GetTransition(JobSlotWorkflowAttribute.CancelStep).TakesNoEdits);

        Assert.All(Jobs.Transitions.Where(transition => transition.From != transition.To && transition.To is JobState.InProgress or JobState.Completed), transition => Assert.True(transition.IsSystemDriven));
        Assert.All(
            Jobs.Transitions.Where(transition => !transition.IsSystemDriven && transition.To is JobState.CanceledByCustomer or JobState.CanceledBySupplier),
            transition => Assert.Equal(
                (RolePermission.JobsCancel, true, false, Staffora.Core.Entities.Comments.CommentAudience.Internal),
                (transition.RequiredPermission!.Value, transition.TakesNoEdits, transition.ConfirmationMessage is not null, transition.CommentPrompt!.Audience)));
        Assert.All(Jobs.Transitions.Where(transition => transition.IsSystemDriven), transition => Assert.Null(transition.CommentPrompt));
        Assert.Equal([(JobWorkflowAttribute.DeleteStep, true)], Jobs.Transitions.Where(transition => transition.To == JobState.Deleted).Select(transition => (transition.Key, transition.ConfirmationMessage is not null)));
        Assert.All(Slots.Transitions.Where(transition => transition.To == JobSlotState.Deleted), transition => Assert.True(transition.IsSystemDriven));
        Assert.Equal([JobState.Coordinating, JobState.Confirmed, JobState.InProgress], JobRules.StaffingOpenStates);
        Assert.Equal([JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating], JobRules.SlotsOpenStates);
    }

    [Fact]
    public void The_fields_follow_the_state_and_the_coordinator_and_the_cross_tenant_links_are_no_dialogs()
    {
        foreach (var state in new[] { JobState.Create, JobState.Registration, JobState.AwaitingCoordination })
        {
            var fields = Jobs.GetFieldConfig(new Job { State = state });
            Assert.Equal(FieldAccessFlags.Required, fields.GetFieldAccess(nameof(Job.CustomerId)));
            Assert.Equal(FieldAccessFlags.ReadWrite, fields.GetFieldAccess(nameof(Job.ContactId)));
            Assert.Equal(FieldAccessFlags.Required, fields.GetFieldAccess(nameof(Job.PeriodStart)));
            Assert.Equal(FieldAccessFlags.ReadWrite, fields.GetAreaAccess(JobWorkflowAttribute.AreaTimetable));
        }

        var coordinating = Jobs.GetFieldConfig(new Job { State = JobState.Coordinating });
        Assert.Equal(FieldAccessFlags.ReadOnly, coordinating.GetFieldAccess(nameof(Job.CustomerId)));
        Assert.Equal(FieldAccessFlags.Required, coordinating.GetFieldAccess(nameof(Job.OrganizationUnitId)));
        Assert.Equal(FieldAccessFlags.ReadWrite, coordinating.GetAreaAccess(JobWorkflowAttribute.AreaSlots));
        var confirmed = Jobs.GetFieldConfig(new Job { State = JobState.Confirmed });
        Assert.Equal(FieldAccessFlags.ReadOnly, confirmed.GetFieldAccess(nameof(Job.PeriodEnd)));
        Assert.Equal(FieldAccessFlags.ReadWrite, confirmed.GetFieldAccess(nameof(Job.ContactId)));
        Assert.Equal(FieldAccessFlags.ReadOnly, confirmed.GetAreaAccess(JobWorkflowAttribute.AreaTimetable));
        Assert.Equal(FieldAccessFlags.ReadOnly, confirmed.GetAreaAccess(JobWorkflowAttribute.AreaSlots));
        Assert.Equal(FieldAccessFlags.ReadOnly, Jobs.GetFieldConfig(new Job { State = JobState.Create }).GetAreaAccess(JobWorkflowAttribute.AreaSlots));
        foreach (var state in Enum.GetValues<JobState>())
        {
            var fields = Jobs.GetFieldConfig(new Job { State = state });
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Job.CoordinatorUserId)));
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Job.CrossTenantId)));
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Job.LinkedCrossTenantId)));
        }

        Assert.Equal(FieldAccessFlags.ReadWrite, Slots.GetFieldConfig(new JobSlot { State = JobSlotState.Active }).GetAreaAccess(JobSlotWorkflowAttribute.AreaQualifications));
        Assert.Equal(FieldAccessFlags.ReadOnly, Slots.GetFieldConfig(new JobSlot { State = JobSlotState.Canceled }).GetAreaAccess(JobSlotWorkflowAttribute.AreaQualifications));
    }

    [Fact]
    public void A_registration_needs_an_active_customer_an_active_contact_of_it_and_a_unit_in_use()
    {
        var environment = InTenant(Tenant);
        Assert.Empty(Jobs.GetBlockingReasons(New(), JobWorkflowAttribute.RegisterStep, environment));
        Assert.Contains(JobWorkflowAttribute.ChosenCustomerReason, Jobs.GetBlockingReasons(New(customerState: CustomerState.Inactive), JobWorkflowAttribute.RegisterStep, environment));
        Assert.Contains(JobWorkflowAttribute.ChosenContactReason, Jobs.GetBlockingReasons(New(contactState: CustomerContactState.Inactive), JobWorkflowAttribute.RegisterStep, environment));
        Assert.Contains(JobWorkflowAttribute.ChosenUnitReason, Jobs.GetBlockingReasons(New(unitState: OrganizationUnitState.Inactive), JobWorkflowAttribute.RegisterStep, environment));

        var otherCustomers = New();
        otherCustomers.Contact!.CustomerId = Guid.NewGuid();
        Assert.Contains(JobWorkflowAttribute.ContactOfCustomerReason, Jobs.GetBlockingReasons(otherCustomers, JobWorkflowAttribute.RegisterStep, environment));
        var forged = New();
        forged.ContactId = Guid.NewGuid();
        Assert.Contains(JobWorkflowAttribute.ContactOfCustomerReason, Jobs.GetBlockingReasons(forged, JobWorkflowAttribute.RegisterStep, environment));

        Assert.NotEmpty(Jobs.GetBlockingReasons(New(), JobWorkflowAttribute.RegisterStep, new WorkflowEnvironment(new FakeClock(Environments.Noon), NavigationLoadState.None, new Session(new RuntimeExecutionContext(Tenant, Actor), new FixedGuard(null)))));
    }

    [Fact]
    public void A_save_keeps_what_the_stored_job_names_and_chooses_anew_only_what_is_in_use()
    {
        var job = New(JobState.Registration, customerState: CustomerState.Inactive, contactState: CustomerContactState.Inactive, unitState: OrganizationUnitState.Inactive);
        var kept = InTenant(Tenant, new StoredJobReferences(job.CustomerId, job.ContactId, job.OrganizationUnitId));
        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveRegistrationStep, kept));

        var chosen = InTenant(Tenant, new StoredJobReferences(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(
            [JobWorkflowAttribute.ChosenCustomerReason, JobWorkflowAttribute.ChosenContactReason, JobWorkflowAttribute.ChosenUnitReason],
            Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveRegistrationStep, chosen));

        Assert.ThrowsAny<InvalidOperationException>(() => Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveRegistrationStep, InTenant(Tenant)));

        var handOver = Jobs.GetBlockingReasons(job, JobWorkflowAttribute.HandOverStep, kept);
        Assert.Contains(JobWorkflowAttribute.CustomerActiveReason, handOver);
        Assert.Contains(JobWorkflowAttribute.ContactActiveReason, handOver);
        Assert.Contains(JobWorkflowAttribute.UnitInUseReason, handOver);
        var ready = New(JobState.Registration);
        var readyKept = InTenant(Tenant, new StoredJobReferences(ready.CustomerId, ready.ContactId, ready.OrganizationUnitId));
        Assert.Equal([JobWorkflowAttribute.HasSlotReason], Jobs.GetBlockingReasons(ready, JobWorkflowAttribute.StartFromRegistrationStep, readyKept));
        ready.Slots.Add(new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, State = JobSlotState.Canceled, Name = "Spare" });
        Assert.Equal([JobWorkflowAttribute.HasSlotReason], Jobs.GetBlockingReasons(ready, JobWorkflowAttribute.StartFromRegistrationStep, readyKept));
        ready.Slots.Add(new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, State = JobSlotState.Active, Name = "Lead" });
        Assert.Empty(Jobs.GetBlockingReasons(ready, JobWorkflowAttribute.StartFromRegistrationStep, readyKept));
        ready.TimeSlots.Clear();
        Assert.Equal([JobWorkflowAttribute.HasOccurrenceReason], Jobs.GetBlockingReasons(ready, JobWorkflowAttribute.StartFromRegistrationStep, readyKept));
    }

    [Fact]
    public async Task Starting_coordination_names_the_user_who_starts_it_and_without_a_session_fails()
    {
        foreach (var step in new[] { JobWorkflowAttribute.StartFromRegistrationStep, JobWorkflowAttribute.StartStep })
        {
            var job = New(step == JobWorkflowAttribute.StartStep ? JobState.AwaitingCoordination : JobState.Registration);
            job.Slots.Add(new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, State = JobSlotState.Active, Name = "Lead" });
            var environment = InTenant(Tenant, new StoredJobReferences(job.CustomerId, job.ContactId, job.OrganizationUnitId));
            Assert.Empty(Jobs.GetBlockingReasons(job, step, environment));
            await Jobs.ExecuteTransitionAsync(job, step, environment, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal((JobState.Coordinating, Actor), (job.State, job.CoordinatorUserId));
        }

        var unattended = New(JobState.Registration);
        await Assert.ThrowsAnyAsync<Exception>(() => Jobs.ExecuteTransitionAsync(unattended, JobWorkflowAttribute.StartFromRegistrationStep, Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(unattended.CoordinatorUserId);

        Assert.Single(Jobs.GetTransition(JobWorkflowAttribute.ConfirmStep).Actions);
    }

    [Fact]
    public async Task A_portal_job_tells_its_portal_each_customer_visible_state_it_enters_and_nothing_else()
    {
        foreach (var transition in Jobs.Transitions)
        {
            var tells = transition.From != transition.To && OrderSync.IsCustomerVisible(transition.To);
            Assert.Equal(tells, transition.Actions.OfType<Staffora.Core.Workflow.Actions.DelegateAction<JobState, Job>>().Any());
        }

        foreach (var (from, step, to) in new[]
        {
            (JobState.Coordinating, JobWorkflowAttribute.CancelStepFrom(JobState.Coordinating), JobState.CanceledBySupplier),
            (JobState.Confirmed, JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.Confirmed), JobState.CanceledByCustomer),
        })
        {
            var own = New(from);
            var sync = new RecordingSync();
            var untold = await Jobs.ExecuteTransitionAsync(own, step, InTenant(Tenant, Kept(own), sync: sync), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(untold.BeforeSave);

            var portal = New(from);
            portal.CrossTenantId = Guid.NewGuid();
            var told = await Jobs.ExecuteTransitionAsync(portal, step, InTenant(Tenant, Kept(portal), sync: sync), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(to, portal.State);
            Assert.Empty(sync.Queued);
            await Assert.Single(told.BeforeSave)(null!, TestContext.Current.CancellationToken);
            Assert.Equal([(portal.Id, from, to)], sync.Queued);

            var unsynced = New(from);
            unsynced.CrossTenantId = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<Exception>(() => Jobs.ExecuteTransitionAsync(unsynced, step, InTenant(Tenant, Kept(unsynced)), cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_job_canceled_in_its_portal_tells_its_coordinator_and_falls_back_to_every_coordinator()
    {
        var coordinated = New(JobState.Coordinating);
        coordinated.CrossTenantId = Guid.NewGuid();
        coordinated.Coordinator = new ApplicationUser { Id = Guid.NewGuid(), State = UserState.Active };
        coordinated.CoordinatorUserId = coordinated.Coordinator.Id;
        var outcome = await Jobs.ExecuteTransitionAsync(coordinated, JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.Coordinating), InTenant(Tenant, Kept(coordinated), sync: new RecordingSync()), cancellationToken: TestContext.Current.CancellationToken);
        var named = Assert.Single(outcome.OutgoingMessages);
        Assert.Equal(
            (MessageTypes.JobCanceledInPortal.Key, (RolePermission?)null, (RolePermission?)RolePermission.ViewCoordination, (RolePermission?)RolePermission.PerformCoordination),
            (named.MessageType, named.RecipientPermission, named.NamedRecipientsHold, named.FallbackPermission));
        Assert.Equal([coordinated.CoordinatorUserId!.Value], named.RecipientUserIds);
        Assert.Equal($"?dlg=job-{coordinated.Id}", named.Link);

        var waiting = New(JobState.AwaitingCoordination);
        waiting.CrossTenantId = Guid.NewGuid();
        var everyone = Assert.Single((await Jobs.ExecuteTransitionAsync(waiting, JobWorkflowAttribute.CanceledInPortalStepFrom(JobState.AwaitingCoordination), InTenant(Tenant, Kept(waiting), sync: new RecordingSync()), cancellationToken: TestContext.Current.CancellationToken)).OutgoingMessages);
        Assert.Equal((MessageTypes.JobCanceledInPortal.Key, (RolePermission?)null, (RolePermission?)RolePermission.PerformCoordination), (everyone.MessageType, everyone.RecipientPermission, everyone.FallbackPermission));
        Assert.Empty(everyone.RecipientUserIds);
    }

    [Fact]
    public void A_job_its_customers_portal_sent_keeps_that_customer()
    {
        var job = New(JobState.AwaitingCoordination);
        job.LinkedCrossTenantId = Guid.NewGuid();
        var stored = new StoredJobReferences(Guid.NewGuid(), job.ContactId, job.OrganizationUnitId);
        Assert.Contains(JobWorkflowAttribute.PortalCustomerKeptReason, Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveAwaitingStep, InTenant(Tenant, stored)));
        Assert.DoesNotContain(JobWorkflowAttribute.PortalCustomerKeptReason, Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveAwaitingStep, InTenant(Tenant, Kept(job))));

        job.LinkedCrossTenantId = null;
        Assert.DoesNotContain(JobWorkflowAttribute.PortalCustomerKeptReason, Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveAwaitingStep, InTenant(Tenant, stored)));
    }

    private static StoredJobReferences Kept(Job job) => new(job.CustomerId, job.ContactId, job.OrganizationUnitId);

    private sealed class RecordingSync : IJobStateSync
    {
        public List<(Guid Job, JobState From, JobState To)> Queued { get; } = [];

        public Task QueueStateAsync(Microsoft.EntityFrameworkCore.DbContext store, Job job, JobState from, CancellationToken cancellationToken)
        {
            Queued.Add((job.Id, from, job.State));
            return Task.CompletedTask;
        }

        public Task QueueStaffMembersAsync(Microsoft.EntityFrameworkCore.DbContext store, SlotAssignment assignment, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task QueueAnswerAsync(Microsoft.EntityFrameworkCore.DbContext store, Job job, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void A_slot_changes_only_while_its_job_takes_slots()
    {
        var environment = InTenant(Tenant);
        foreach (var state in Enum.GetValues<JobState>())
        {
            var slot = Slot(state);
            Assert.Equal(JobRules.SlotsOpen(state), Slots.GetBlockingReasons(slot, JobSlotWorkflowAttribute.SaveStep, environment).Count == 0);
            Assert.Equal(JobRules.SlotsOpen(state), Slots.GetBlockingReasons(slot, JobSlotWorkflowAttribute.CancelStep, environment).Count == 0);
        }

        var forged = Slot(JobState.Registration);
        forged.JobId = Guid.NewGuid();
        Assert.Equal([JobSlotWorkflowAttribute.JobTakesSlotsReason], Slots.GetBlockingReasons(forged, JobSlotWorkflowAttribute.SaveStep, environment));
    }

    [Fact]
    public void Every_step_needs_a_session_inside_the_records_own_tenant()
    {
        var job = New(JobState.Registration);
        var stored = new StoredJobReferences(job.CustomerId, job.ContactId, job.OrganizationUnitId);
        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveRegistrationStep, InTenant(Tenant, stored)));
        Assert.Equal([JobWorkflowAttribute.OwnTenantReason], Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveRegistrationStep, InTenant(Guid.NewGuid(), stored)));
        Assert.Equal([JobWorkflowAttribute.OwnTenantReason], Slots.GetBlockingReasons(Slot(JobState.Registration), JobSlotWorkflowAttribute.SaveStep, InTenant(Guid.NewGuid())));
    }

    private static Job New(JobState state = JobState.Create, CustomerState customerState = CustomerState.Active, CustomerContactState contactState = CustomerContactState.Active, OrganizationUnitState unitState = OrganizationUnitState.Active)
    {
        var customer = new Customer { Id = Guid.NewGuid(), TenantId = Tenant, State = customerState, Name = "Brightpath Learning" };
        var contact = new CustomerContact { Id = Guid.NewGuid(), TenantId = Tenant, State = contactState, CustomerId = customer.Id, Customer = customer, FirstName = "Ada", LastName = "Moreno" };
        var unit = new OrganizationUnit { Id = Guid.NewGuid(), TenantId = Tenant, State = unitState, Name = "Branch East", UnitType = OrganizationUnitType.Branch };
        return new Job
        {
            Id = state == JobState.Create ? Guid.Empty : Guid.NewGuid(),
            TenantId = Tenant,
            State = state,
            Name = "Algebra",
            CustomerId = customer.Id,
            Customer = customer,
            ContactId = contact.Id,
            Contact = contact,
            OrganizationUnitId = unit.Id,
            OrganizationUnit = unit,
            PeriodStart = new DateOnly(2027, 1, 11),
            PeriodEnd = new DateOnly(2027, 5, 28),
            TimeSlots = [new JobTimeSlot { Weekday = Weekday.Monday, Start = new TimeOnly(8, 0), End = new TimeOnly(9, 0) }],
        };
    }

    private static JobSlot Slot(JobState jobState)
    {
        var job = new Job { Id = Guid.NewGuid(), TenantId = Tenant, State = jobState, Name = "Algebra" };
        return new JobSlot { Id = Guid.NewGuid(), TenantId = Tenant, State = JobSlotState.Active, JobId = job.Id, Job = job, Name = "Lead" };
    }

    [Fact]
    public void The_clocks_steps_judge_the_period_on_the_job_tenants_calendar_and_fail_closed_without_its_zone()
    {
        var stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        var job = new Job { Id = Guid.NewGuid(), TenantId = Tenant, State = JobState.Confirmed, Name = "Algebra", PeriodStart = new DateOnly(2027, 3, 28), PeriodEnd = new DateOnly(2027, 3, 28) };
        WorkflowEnvironment At(string instant, TimeZoneInfo? zone, Guid? session = null) =>
            new(new FakeClock(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)), NavigationLoadState.Complete, new Session(new RuntimeExecutionContext(session ?? Tenant, Actor), new FixedGuard(null, zone)));

        Assert.Equal([JobWorkflowAttribute.PeriodStartedReason], Jobs.GetBlockingReasons(job, JobWorkflowAttribute.PeriodStartsStep, At("2027-03-27T22:59:59Z", stockholm)));
        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.PeriodStartsStep, At("2027-03-27T23:00:00Z", stockholm)));
        Assert.Equal([JobWorkflowAttribute.PeriodStartedReason], Jobs.GetBlockingReasons(job, JobWorkflowAttribute.PeriodStartsStep, At("2027-03-27T23:00:00Z", TimeZoneInfo.Utc)));

        Assert.Equal([JobWorkflowAttribute.OwnTenantReason], Jobs.GetBlockingReasons(job, JobWorkflowAttribute.PeriodStartsStep, At("2027-03-27T23:00:00Z", stockholm, Guid.NewGuid())));

        job.State = JobState.InProgress;
        Assert.Equal([JobWorkflowAttribute.PeriodEndedReason], Jobs.GetBlockingReasons(job, JobWorkflowAttribute.PeriodEndsStep, At("2027-03-28T21:59:59Z", stockholm)));
        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.PeriodEndsStep, At("2027-03-28T22:00:00Z", stockholm)));

        Assert.ThrowsAny<InvalidOperationException>(() => Jobs.GetBlockingReasons(job, JobWorkflowAttribute.PeriodEndsStep, At("2027-04-28T22:00:00Z", zone: null)));
    }

    [Fact]
    public void A_step_with_edits_is_refused_with_each_clash_the_guard_finds_for_the_jobs_holders_and_a_new_job_asks_nothing()
    {
        var job = New(JobState.Coordinating);
        var stored = new StoredJobReferences(job.CustomerId, job.ContactId, job.OrganizationUnitId);
        var clashes = new[] { "Ada Example is not free for 1 occurrence the change adds. Assigned to work a job: 1 occurrence, 2027-03-08." };
        var refused = Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveCoordinatingStep, Clashing(stored, clashes));
        Assert.Equal(clashes, refused);
        Assert.Equal(clashes, Jobs.GetBlockingReasons(job, JobWorkflowAttribute.ConfirmStep, Clashing(stored, clashes)));
        Assert.Empty(Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveCoordinatingStep, Clashing(stored, [])));

        Assert.ThrowsAny<InvalidOperationException>(() => Jobs.GetBlockingReasons(job, JobWorkflowAttribute.SaveCoordinatingStep, Clashing(stored, null)));

        Assert.Empty(Jobs.GetBlockingReasons(New(), JobWorkflowAttribute.RegisterStep, Clashing(null, null)));
    }

    private static WorkflowEnvironment Clashing(StoredJobReferences? stored, IReadOnlyList<string>? clashes) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Session(new RuntimeExecutionContext(Tenant, Actor), new FixedGuard(stored, clashes: clashes, clashesUnread: clashes is null)), customFields: CustomFieldCatalog.Of(typeof(JobAttributeDefinition), []));

    private static WorkflowEnvironment InTenant(Guid tenantId, StoredJobReferences? stored = null, IReadOnlyList<IEntityAttributeDefinition>? fields = null, IJobStateSync? sync = null) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Session(new RuntimeExecutionContext(tenantId, Actor), new FixedGuard(stored), sync), customFields: CustomFieldCatalog.Of(typeof(JobAttributeDefinition), fields ?? []));

    private sealed class Session : IServiceProvider
    {
        private readonly IRuntimeExecutionContext runtime;
        private readonly IJobGuard guard;
        private readonly IJobStateSync? sync;

        public Session(IRuntimeExecutionContext runtime, IJobGuard guard, IJobStateSync? sync = null)
        {
            this.runtime = runtime;
            this.guard = guard;
            this.sync = sync;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRuntimeExecutionContext) ? this.runtime
            : serviceType == typeof(IJobGuard) ? this.guard
            : serviceType == typeof(IJobStateSync) ? this.sync
            : null;
    }

    private sealed class FixedGuard : IJobGuard
    {
        private readonly StoredJobReferences? stored;
        private readonly TimeZoneInfo? zone;
        private readonly IReadOnlyList<string>? clashes;
        private readonly bool clashesUnread;

        public FixedGuard(StoredJobReferences? stored, TimeZoneInfo? zone = null, IReadOnlyList<string>? clashes = null, bool clashesUnread = false)
        {
            this.stored = stored;
            this.zone = zone;
            this.clashes = clashes;
            this.clashesUnread = clashesUnread;
        }

        public StoredJobReferences StoredReferences(Job job) =>
            this.stored ?? throw new InvalidOperationException("Nothing was read about the job.");

        public TimeZoneInfo TimeZoneOf(Job job) =>
            this.zone ?? throw new InvalidOperationException("No time zone was read for the job.");

        public IReadOnlyList<string> HolderClashes(Job job) =>
            this.clashesUnread ? throw new InvalidOperationException("Nothing was read about the job's holders.") : this.clashes ?? [];
    }
}
