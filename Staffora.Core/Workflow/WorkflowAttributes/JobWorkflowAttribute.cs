using Staffora.Core.Jobs;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class JobWorkflowAttribute : WorkflowAttribute
{
    public const string AreaTimetable = "Timetable";

    public const string AreaPlace = "Place";

    public const string AreaSlots = "Slots";

    public const string AreaAttributes = WorkflowAreas.Attributes;

    public const string DeleteStep = "job-delete";

    public const string NoStaffAskedReason = "Nobody was asked to work the job: a job that staff members were asked for is kept for their record, so cancel it instead";

    public const string RegisterStep = "job-register";

    public const string SaveRegistrationStep = "job-save-registration";

    public const string HandOverStep = "job-hand-over";

    public const string SaveAwaitingStep = "job-save-awaiting";

    public const string BackToRegistrationStep = "job-back-to-registration";

    public const string StartStep = "job-start";

    public const string StartFromRegistrationStep = "job-start-from-registration";

    public const string SaveCoordinatingStep = "job-save-coordinating";

    public const string ConfirmStep = "job-confirm";

    public const string SaveConfirmedStep = "job-save-confirmed";

    public const string SaveInProgressStep = "job-save-in-progress";

    public const string PeriodStartsStep = "job-period-starts";

    public const string PeriodEndsStep = "job-period-ends";

    public const string ReceiveStep = "job-receive";

    public const string PeriodStartedReason = "The period has started on the organization's calendar";

    public const string PeriodEndedReason = "The period's last day is over on the organization's calendar";

    public const string OwnTenantReason = "Only a user of this organization changes its jobs";

    public const string ChosenCustomerReason = "The customer chosen is active";

    public const string ChosenContactReason = "The contact chosen is active";

    public const string ChosenUnitReason = "The unit chosen is in use";

    public const string ContactOfCustomerReason = "The contact is one of the job's customer's";

    public const string CustomerActiveReason = "The customer is active";

    public const string ContactActiveReason = "The contact is active";

    public const string UnitInUseReason = "The unit is in use";

    public const string HasOccurrenceReason = "The timetable has at least one occurrence";

    public const string NothingSetAheadReason = "A new job names no coordinator and no link to another organization's job: later steps set them";

    public const string HasSlotReason = "The job needs at least one staff member";

    public const string PortalCustomerKeptReason = "A job a customer sent through its portal keeps that customer";

    public const string HoldersFreeReason = "Every staff member assigned to the job is free for the times the change adds";

    private const int Back = 10;
    private const int Neutral = 20;
    private const int Forward = 30;
    private const int Alternative = 25;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<JobState, Job>();

        workflow.AddState(JobState.Create).ConfigureFields(f => Prepared(f, slots: FieldAccessFlags.ReadOnly));
        workflow.AddState(JobState.Registration).ConfigureFields(f => Prepared(f, slots: FieldAccessFlags.ReadWrite));
        workflow.AddState(JobState.AwaitingCoordination).ConfigureFields(f => Prepared(f, slots: FieldAccessFlags.ReadWrite));

        workflow.AddState(JobState.Coordinating)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Name, FieldAccessFlags.Required)
                .Field(e => e.Description, FieldAccessFlags.ReadWrite)
                .Field(e => e.ContactId, FieldAccessFlags.ReadWrite)
                .Field(e => e.OrganizationUnitId, FieldAccessFlags.Required)
                .Field(e => e.PeriodStart, FieldAccessFlags.Required)
                .Field(e => e.PeriodEnd, FieldAccessFlags.Required)
                .Field(e => e.Schedule, FieldAccessFlags.Required)
                .Field(e => e.StartTime, FieldAccessFlags.ReadWrite)
                .Field(e => e.EndTime, FieldAccessFlags.ReadWrite)
                .Area(AreaTimetable, FieldAccessFlags.ReadWrite, a => a.Field(e => e.TimeSlots))
                .Area(AreaSlots, FieldAccessFlags.ReadWrite)
                .Area(AreaAttributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes))
                .Area(AreaPlace, FieldAccessFlags.ReadWrite, PlaceFields));

        foreach (var fixedState in new[] { JobState.Confirmed, JobState.InProgress })
        {
            workflow.AddState(fixedState)
                .ConfigureFields(f => f
                    .Default(FieldAccessFlags.ReadOnly)
                    .Field(e => e.Name, FieldAccessFlags.Required)
                    .Field(e => e.Description, FieldAccessFlags.ReadWrite)
                    .Field(e => e.ContactId, FieldAccessFlags.ReadWrite)
                    .Area(AreaTimetable, FieldAccessFlags.ReadOnly, a => a.Field(e => e.TimeSlots))
                    .Area(AreaSlots, FieldAccessFlags.ReadOnly)
                    .Area(AreaAttributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes))
                    .Area(AreaPlace, FieldAccessFlags.ReadWrite, PlaceFields));
        }

        foreach (var kept in new[] { JobState.Completed, JobState.CanceledByCustomer, JobState.CanceledBySupplier, JobState.Deleted })
        {
            var state = workflow.AddState(kept)
                .ConfigureFields(f => f
                    .Default(FieldAccessFlags.ReadOnly)
                    .Area(AreaTimetable, FieldAccessFlags.ReadOnly, a => a.Field(e => e.TimeSlots))
                    .Area(AreaSlots, FieldAccessFlags.ReadOnly)
                    .Area(AreaAttributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes))
                    .Area(AreaPlace, FieldAccessFlags.ReadOnly, PlaceFields));

            if (kept is JobState.CanceledByCustomer or JobState.CanceledBySupplier)
            {
                state.Cascade(e => e.Slots, JobSlotState.Canceled);
            }
            else if (kept == JobState.Deleted)
            {
                state.Cascade(e => e.Slots, JobSlotState.Deleted);
            }
        }

        AddSaveRules(workflow.AddTransition(new(RegisterStep, JobState.Create, JobState.Registration)
        {
            Label = "Register",
            Description = "Register the job for its customer: its timetable and slots are completed before it is handed over for coordination",
            Icon = "edit_note",
            RequiredPermission = RolePermission.JobsCreate,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })).AddCondition(new DelegateCondition<JobState, Job>(NothingSetAheadReason, e => e.CoordinatorUserId is null && e.CrossTenantId is null && e.LinkedCrossTenantId is null));

        AddSave(workflow, SaveRegistrationStep, JobState.Registration, "Save the job's details and timetable. It stays in registration");

        AddReadyRules(AddSaveRules(workflow.AddTransition(new(HandOverStep, JobState.Registration, JobState.AwaitingCoordination)
        {
            Label = "Hand over",
            Description = "Hand the job over for coordination: a coordinator starts staffing it",
            Icon = "forward_to_inbox",
            RequiredPermission = RolePermission.JobsCreate,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })));

        AddSave(workflow, SaveAwaitingStep, JobState.AwaitingCoordination, "Save the job's details and timetable. It still waits for coordination");

        AddSaveRules(workflow.AddTransition(new(BackToRegistrationStep, JobState.AwaitingCoordination, JobState.Registration)
        {
            Label = "Back to registration",
            Description = "Take the job back into registration: it no longer waits for a coordinator",
            Icon = "undo",
            RequiredPermission = RolePermission.JobsEdit,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        }));

        AddReadyRules(AddSaveRules(workflow.AddTransition(new(StartStep, JobState.AwaitingCoordination, JobState.Coordinating)
        {
            Label = "Start coordination",
            Description = "Start staffing the job: you become its coordinator, and staff members can be requested and assigned",
            Icon = "edit_calendar",
            RequiredPermission = RolePermission.PerformCoordination,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        }))).AddAction(NameCoordinator());

        AddReadyRules(AddSaveRules(workflow.AddTransition(new(StartFromRegistrationStep, JobState.Registration, JobState.Coordinating)
        {
            Label = "Start coordination",
            Description = "Start staffing the job at once, without handing it over: you become its coordinator",
            Icon = "edit_calendar",
            RequiredPermission = RolePermission.PerformCoordination,
            SortOrder = Alternative,
        }))).AddAction(NameCoordinator());

        AddSave(workflow, SaveCoordinatingStep, JobState.Coordinating, "Save the job's details and timetable. Its staffing goes on");

        AddSaveRules(workflow.AddTransition(new(ConfirmStep, JobState.Coordinating, JobState.Confirmed)
        {
            Label = "Confirm",
            Description = "Confirm the job as staffed: its period, timetable and slots are fixed from now on",
            Icon = "event_available",
            ConfirmationMessage = "Confirm this job? Its period, timetable and slots can no longer be changed, but staff members can still be requested and assigned.",
            RequiredPermission = RolePermission.PerformCoordination,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })).AddCondition(new DelegateCondition<JobState, Job>(HasOccurrenceReason, e => JobRules.HasTime(JobTimes.Of(e)), nameof(Job.TimeSlots)))
            .AddCondition(new RequiredAttributesCondition<JobState, Job, JobAttribute>(e => e.Attributes));

        AddSave(workflow, SaveConfirmedStep, JobState.Confirmed, "Save the job's name, description and contact. It stays confirmed");
        AddSave(workflow, SaveInProgressStep, JobState.InProgress, "Save the job's name, description and contact. It goes on");

        workflow.AddTransition(new(PeriodStartsStep, JobState.Confirmed, JobState.InProgress)
        {
            Label = "Period started",
            Description = "The job's period has started: the system puts it in progress at the start of the period's first day, on the organization's calendar",
            Icon = "play_circle",
            IsSystemDriven = true,
            SortOrder = Forward,
        }).AddCondition(PeriodHasStarted());

        workflow.AddTransition(new(PeriodEndsStep, JobState.InProgress, JobState.Completed)
        {
            Label = "Period ended",
            Description = "The job's period is over: the system completes it once the period's last day has passed, on the organization's calendar",
            Icon = "task_alt",
            IsSystemDriven = true,
            SortOrder = Forward,
        }).AddCondition(PeriodHasEnded());

        workflow.AddTransition(new(ReceiveStep, JobState.Create, JobState.AwaitingCoordination)
        {
            Label = "Received",
            Description = "The customer sent the job through its portal: it waits for a coordinator, who completes its timetable and slots",
            Icon = "move_to_inbox",
            IsSystemDriven = true,
            SortOrder = Forward,
        });

        foreach (var (from, _) in Cancelable)
        {
            workflow.AddTransition(new(CanceledInPortalStepFrom(from), from, JobState.CanceledByCustomer)
            {
                Label = "Canceled in the portal",
                Description = "The customer canceled its order through its portal: the job's slots and every staff member asked for or assigned to it are canceled, each staff member told",
                Icon = "event_busy",
                IsSystemDriven = true,
                SortOrder = Back,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            })
            .AddAction(CoordinatorNotice());
        }

        workflow.AddTransition(new(DeleteStep, JobState.Registration, JobState.Deleted)
        {
            Label = "Delete",
            Description = "Delete the job with its slots: it was never staffed, and its history is kept",
            Icon = "delete",
            ConfirmationMessage = "Delete this job? It is removed with its timetable and slots, but its history is kept.",
            RequiredPermission = RolePermission.JobsDelete,
            ShowWhenBlocked = true,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        }).AddCondition(new DelegateCondition<JobState, Job>(NoStaffAskedReason, e => e.Slots.All(slot => slot.Assignments.Count == 0), $"{nameof(Job.Slots)}.{nameof(JobSlot.Assignments)}"));

        foreach (var (from, _) in Cancelable)
        {
            workflow.AddTransition(new(CancelStepFrom(from), from, JobState.CanceledBySupplier)
            {
                Label = "Cancel job",
                Description = "Cancel the job on our side: its slots and every staff member asked for or assigned to it are canceled, each staff member told",
                Icon = "cancel",
                CommentPrompt = CancelPrompt,
                RequiredPermission = RolePermission.JobsCancel,
                SortOrder = Back,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            });
            workflow.AddTransition(new(CustomerCancelStepFrom(from), from, JobState.CanceledByCustomer)
            {
                Label = "Canceled by the customer",
                Description = "Record that the customer canceled the job: its slots and every staff member asked for or assigned to it are canceled, each staff member told",
                Icon = "event_busy",
                CommentPrompt = CustomerCancelPrompt,
                RequiredPermission = RolePermission.JobsCancel,
                SortOrder = Back,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            });
        }

        foreach (var transition in workflow.Transitions.Where(transition => transition.From != transition.To && OrderSync.IsCustomerVisible(transition.To)))
        {
            transition.AddAction(TellThePortal());
        }

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    public static readonly TransitionCommentPrompt CancelPrompt = new("Cancel the job")
    {
        Message = "Its slots are canceled, and every staff member asked for or assigned to it is told. The job is kept, canceled. Why is it canceled? Only the coordinators read the reason, in the job's notes.",
        Placeholder = "The reason",
        Audience = CommentAudience.Internal,
    };

    public static readonly TransitionCommentPrompt CustomerCancelPrompt = new("Record the customer's cancellation")
    {
        Message = "Its slots are canceled, and every staff member asked for or assigned to it is told. The job is kept, canceled by the customer. Why did the customer cancel? Only the coordinators read the reason, in the job's notes.",
        Placeholder = "The reason",
        Audience = CommentAudience.Internal,
    };

    public static readonly IReadOnlyList<(JobState State, string Slug)> Cancelable =
    [
        (JobState.Registration, "registration"),
        (JobState.AwaitingCoordination, "awaiting"),
        (JobState.Coordinating, "coordinating"),
        (JobState.Confirmed, "confirmed"),
        (JobState.InProgress, "in-progress"),
    ];

    public static string CancelStepFrom(JobState state) => $"job-cancel-{SlugOf(state)}";

    public static string CustomerCancelStepFrom(JobState state) => $"job-canceled-by-customer-{SlugOf(state)}";

    public static string CanceledInPortalStepFrom(JobState state) => $"job-canceled-in-portal-{SlugOf(state)}";

    private static DelegateAction<JobState, Job> TellThePortal() => new(context =>
    {
        if (context.Entity.CrossTenantId is null)
        {
            return;
        }

        var sync = context.ServiceProvider?.GetService(typeof(IJobStateSync)) as IJobStateSync
            ?? throw new InvalidOperationException("A job a customer's portal sent moves only where its portal can be told.");
        var (job, from) = (context.Entity, context.Transition.From);
        context.BeforeSave.Add((store, token) => sync.QueueStateAsync(store, job, from, token));
    });

    private static NotifyAction<JobState, Job> CoordinatorNotice() =>
        new(MessageTypes.JobCanceledInPortal,
            job => new Dictionary<string, string?>
            {
                ["Customer"] = job.Customer?.Name,
                ["Job"] = job.Name,
                ["Period"] = JobRules.WhenText(JobTimes.Of(job)),
                ["JobId"] = job.Id.ToString(),
            },
            recipients: job => job.CoordinatorUserId is { } coordinator ? [coordinator] : [],
            namedRecipientsHold: RolePermission.ViewCoordination,
            fallbackPermission: RolePermission.PerformCoordination);

    private static string SlugOf(JobState state) =>
        Cancelable.FirstOrDefault(cancelable => cancelable.State == state).Slug ?? throw new ArgumentOutOfRangeException(nameof(state), state, "A job in this state is not canceled.");

    private static void AddSave(Workflow<JobState, Job> workflow, string key, JobState state, string description) =>
        AddSaveRules(workflow.AddTransition(new(key, state, state)
        {
            Label = "Save",
            Description = description,
            Icon = "save",
            RequiredPermission = RolePermission.JobsEdit,
            SortOrder = Neutral,
            IsSave = true,
        }));

    private static Transition<JobState, Job> AddSaveRules(Transition<JobState, Job> transition) => transition
        .AddCondition(new RequiredFieldCondition<JobState, Job>(e => e.Name))
        .AddCondition(new RequiredFieldCondition<JobState, Job>(e => e.CustomerId))
        .AddCondition(new RequiredFieldCondition<JobState, Job>(e => e.OrganizationUnitId))
        .AddCondition(new ScheduleValid())
        .AddCondition(ContactOfCustomer())
        .AddCondition(ChosenCustomerInUse())
        .AddCondition(ChosenContactInUse())
        .AddCondition(ChosenUnitInUse())
        .AddCondition(PortalCustomerKept())
        .AddCondition(new HoldersFree());

    private static Transition<JobState, Job> AddReadyRules(Transition<JobState, Job> transition) => transition
        .AddCondition(new RequiredAttributesCondition<JobState, Job, JobAttribute>(e => e.Attributes))
        .AddCondition(new DelegateCondition<JobState, Job>(CustomerActiveReason, e => e.Customer is { State: CustomerState.Active } customer && customer.Id == e.CustomerId, nameof(Job.Customer)))
        .AddCondition(new DelegateCondition<JobState, Job>(ContactActiveReason, e => e.ContactId is null || (e.Contact is { State: CustomerContactState.Active } contact && contact.Id == e.ContactId), nameof(Job.Contact)))
        .AddCondition(new DelegateCondition<JobState, Job>(UnitInUseReason, e => e.OrganizationUnit is { IsInUse: true } unit && unit.Id == e.OrganizationUnitId, nameof(Job.OrganizationUnit)))
        .AddCondition(new DelegateCondition<JobState, Job>(HasOccurrenceReason, e => JobRules.HasTime(JobTimes.Of(e)), nameof(Job.TimeSlots)))
        .AddCondition(new DelegateCondition<JobState, Job>(HasSlotReason, e => e.Slots.Any(slot => slot.State == JobSlotState.Active), nameof(Job.Slots)));

    private static DelegateAction<JobState, Job> NameCoordinator() => new(context =>
        context.Entity.CoordinatorUserId = (context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) as IRuntimeExecutionContext)?.EffectiveUserId
            ?? throw new InvalidOperationException("A job's coordinator is the user who starts its coordination, and the step has no signed-in user."));

    private static DelegateCondition<JobState, Job> InOwnTenant() =>
        DelegateCondition<JobState, Job>.FromContext(OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static DelegateCondition<JobState, Job> PeriodHasStarted() =>
        DelegateCondition<JobState, Job>.FromContext(PeriodStartedReason, context => JobRules.HasStarted(JobTimes.Of(context.Entity), context.TimeProvider.GetUtcNow().UtcDateTime, TenantZone(context)));

    private static DelegateCondition<JobState, Job> PeriodHasEnded() =>
        DelegateCondition<JobState, Job>.FromContext(PeriodEndedReason, context => JobRules.HasEnded(JobTimes.Of(context.Entity), context.TimeProvider.GetUtcNow().UtcDateTime, TenantZone(context)));

    private static TimeZoneInfo TenantZone(WorkflowContext<JobState, Job> context)
    {
        var guard = context.ServiceProvider?.GetService(typeof(IJobGuard)) as IJobGuard
            ?? throw new InvalidOperationException("The job guard is not registered, so the job's tenant's calendar cannot be read.");
        return guard.TimeZoneOf(context.Entity);
    }

    private static DelegateCondition<JobState, Job> ContactOfCustomer() =>
        new(ContactOfCustomerReason, e => e.ContactId is null || (e.Contact is { } contact && contact.Id == e.ContactId && contact.CustomerId == e.CustomerId), nameof(Job.Contact));

    private static DelegateCondition<JobState, Job> ChosenCustomerInUse() =>
        DelegateCondition<JobState, Job>.FromContext(ChosenCustomerReason, context =>
            context.Entity.Customer is { } customer && customer.Id == context.Entity.CustomerId
            && (customer.State == CustomerState.Active || Kept(context, stored => stored.CustomerId == context.Entity.CustomerId)),
            nameof(Job.Customer));

    private static DelegateCondition<JobState, Job> ChosenContactInUse() =>
        DelegateCondition<JobState, Job>.FromContext(ChosenContactReason, context =>
            context.Entity.ContactId is null
            || (context.Entity.Contact is { } contact && contact.Id == context.Entity.ContactId
                && (contact.State == CustomerContactState.Active || Kept(context, stored => stored.ContactId == context.Entity.ContactId))),
            nameof(Job.Contact));

    private static DelegateCondition<JobState, Job> PortalCustomerKept() =>
        DelegateCondition<JobState, Job>.FromContext(PortalCustomerKeptReason, context =>
            context.Entity.LinkedCrossTenantId is null || context.Transition.From == JobState.Create || Kept(context, stored => stored.CustomerId == context.Entity.CustomerId));

    private static DelegateCondition<JobState, Job> ChosenUnitInUse() =>
        DelegateCondition<JobState, Job>.FromContext(ChosenUnitReason, context =>
            context.Entity.OrganizationUnit is { } unit && unit.Id == context.Entity.OrganizationUnitId
            && (unit.IsInUse || Kept(context, stored => stored.OrganizationUnitId == context.Entity.OrganizationUnitId)),
            nameof(Job.OrganizationUnit));

    private static bool Kept(WorkflowContext<JobState, Job> context, Func<StoredJobReferences, bool> kept)
    {
        if (context.Transition.From == JobState.Create)
        {
            return false;
        }

        var guard = context.ServiceProvider?.GetService(typeof(IJobGuard)) as IJobGuard
            ?? throw new InvalidOperationException("The job guard is not registered, so what the stored job names cannot be checked.");
        return kept(guard.StoredReferences(context.Entity));
    }

    private static StateFieldBuilder<Job> Prepared(StateFieldBuilder<Job> fields, FieldAccessFlags slots) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.Description, FieldAccessFlags.ReadWrite)
        .Field(e => e.CustomerId, FieldAccessFlags.Required)
        .Field(e => e.ContactId, FieldAccessFlags.ReadWrite)
        .Field(e => e.OrganizationUnitId, FieldAccessFlags.Required)
        .Field(e => e.PeriodStart, FieldAccessFlags.Required)
        .Field(e => e.PeriodEnd, FieldAccessFlags.Required)
        .Field(e => e.Schedule, FieldAccessFlags.Required)
        .Field(e => e.StartTime, FieldAccessFlags.ReadWrite)
        .Field(e => e.EndTime, FieldAccessFlags.ReadWrite)
        .Area(AreaTimetable, FieldAccessFlags.ReadWrite, a => a.Field(e => e.TimeSlots))
        .Area(AreaSlots, slots)
        .Area(AreaAttributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes))
        .Area(AreaPlace, FieldAccessFlags.ReadWrite, PlaceFields);

    private static void PlaceFields(AreaFieldBuilder<Job> area) => area
        .Field(e => e.PlaceName)
        .Field(e => e.StreetAddress)
        .Field(e => e.PostalCode)
        .Field(e => e.City)
        .Field(e => e.AccessNotes);

    private sealed class ScheduleValid : Condition<JobState, Job>
    {
        public ScheduleValid() : base(JobRules.ScheduleValidReason, [nameof(Job.TimeSlots)])
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<JobState, Job> context)
        {
            var reasons = base.GetBlockingReasons(context);
            return reasons.Count > 0 && reasons[0] == Description ? JobRules.ScheduleProblems(JobTimes.Of(context.Entity)) : reasons;
        }

        protected override bool IsMet(WorkflowContext<JobState, Job> context) => JobRules.ScheduleProblems(JobTimes.Of(context.Entity)).Count == 0;
    }

    private sealed class HoldersFree : Condition<JobState, Job>
    {
        public HoldersFree() : base(HoldersFreeReason, [])
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<JobState, Job> context)
        {
            if (context.Transition.From == JobState.Create)
            {
                return [];
            }

            var guard = context.ServiceProvider?.GetService(typeof(IJobGuard)) as IJobGuard
                ?? throw new InvalidOperationException("The job guard is not registered, so whether the job's staff members stay free cannot be checked.");
            return guard.HolderClashes(context.Entity);
        }

        protected override bool IsMet(WorkflowContext<JobState, Job> context) => GetBlockingReasons(context).Count == 0;
    }
}
