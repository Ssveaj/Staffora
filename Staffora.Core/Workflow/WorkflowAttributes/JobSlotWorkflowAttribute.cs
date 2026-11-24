using Staffora.Core.Jobs;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class JobSlotWorkflowAttribute : WorkflowAttribute
{
    public const string AreaQualifications = "Qualifications";

    public const string SaveStep = "slot-save";

    public const string AddStep = "slot-add";

    public static readonly string CountInRangeReason = $"Between 1 and {JobSlotRules.MaxAddCount} slots are added at once";

    public const string CancelStep = "slot-cancel";

    public const string ReceiveStep = "slot-receive";

    public const string DeleteWithJobStep = "slot-delete-with-job";

    public const string DeleteCanceledWithJobStep = "slot-delete-canceled-with-job";

    public const string JobTakesSlotsReason = "The job takes changes to its slots (it is not confirmed yet)";

    public const string QualificationActiveReason = "Each qualification required anew is an active one of the catalogue";

    public const string ServiceActiveReason = "A slot is made only from an active service of the catalogue";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<JobSlotState, JobSlot>();

        workflow.AddState(JobSlotState.Create)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.None)
                .Field(e => e.Name, FieldAccessFlags.Required)
                .Field(e => e.Description, FieldAccessFlags.ReadWrite)
                .Field(e => e.Count, FieldAccessFlags.Required)
                .Area(AreaQualifications, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Qualifications)));

        workflow.AddState(JobSlotState.Active)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Name, FieldAccessFlags.Required)
                .Field(e => e.Description, FieldAccessFlags.ReadWrite)
                .Field(e => e.Count, FieldAccessFlags.None)
                .Area(AreaQualifications, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Qualifications)));

        foreach (var kept in new[] { JobSlotState.Canceled, JobSlotState.Deleted })
        {
            var state = workflow.AddState(kept)
                .ConfigureFields(f => f
                    .Default(FieldAccessFlags.ReadOnly)
                    .Area(AreaQualifications, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Qualifications)));

            if (kept == JobSlotState.Canceled)
            {
                state.Cascade(e => e.Assignments, SlotAssignmentState.Canceled, SlotAssignmentState.Declined, SlotAssignmentState.NotSelected, SlotAssignmentState.Expired);
            }
        }

        workflow.AddTransition(new(AddStep, JobSlotState.Create, JobSlotState.Active)
        {
            Label = "Add",
            Description = "Add the slots, alike in name and required qualifications, to the job",
            Icon = "add",
            RequiredPermission = RolePermission.JobsEdit,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<JobSlotState, JobSlot>(e => e.Name))
        .AddCondition(new DelegateCondition<JobSlotState, JobSlot>(CountInRangeReason, e => JobSlotRules.CountInRange(e.Count)))
        .AddCondition(JobTakesSlots())
        .AddCondition(QualificationsRequiredAnewActive())
        .AddCondition(ServiceActiveWhenNamed());

        workflow.AddTransition(new(SaveStep, JobSlotState.Active, JobSlotState.Active)
        {
            Label = "Save",
            Description = "Save the slot with the qualifications it requires",
            Icon = "save",
            RequiredPermission = RolePermission.JobsEdit,
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<JobSlotState, JobSlot>(e => e.Name))
        .AddCondition(JobTakesSlots())
        .AddCondition(QualificationsRequiredAnewActive())
        .AddCondition(ServiceActiveWhenNamed());

        workflow.AddTransition(new(CancelStep, JobSlotState.Active, JobSlotState.Canceled)
        {
            Label = "Cancel slot",
            Description = "The job no longer needs this staff member: the slot is kept, canceled",
            Icon = "person_off",
            ConfirmationMessage = "Cancel this slot? It is kept, canceled, and no staff member is staffed for it.",
            RequiredPermission = RolePermission.JobsEdit,
            ShowWhenBlocked = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
            TakesNoEdits = true,
        })
        .AddCondition(JobTakesSlots());

        workflow.AddTransition(new(ReceiveStep, JobSlotState.Create, JobSlotState.Active)
        {
            Label = "Received with the order",
            Description = "The slot came with a customer's order, made from the staffing company's own service",
            Icon = "move_to_inbox",
            IsSystemDriven = true,
            SortOrder = 20,
        });

        foreach (var (key, from) in new[] { (DeleteWithJobStep, JobSlotState.Active), (DeleteCanceledWithJobStep, JobSlotState.Canceled) })
        {
            workflow.AddTransition(new(key, from, JobSlotState.Deleted)
            {
                Label = "Deleted with the job",
                Description = "The slot's job was deleted before it was staffed: the slot goes with it",
                Icon = "delete",
                IsSystemDriven = true,
                SortOrder = 10,
                Placement = TransitionPlacement.Leading,
            });
        }

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    private static DelegateCondition<JobSlotState, JobSlot> JobTakesSlots() =>
        DelegateCondition<JobSlotState, JobSlot>.FromContext(JobTakesSlotsReason, context =>
            context.Entity.Job is { } job && job.Id == context.Entity.JobId && (JobRules.SlotsOpen(job.State) || context.IsTakenWith(job)),
            nameof(JobSlot.Job));

    private static DelegateCondition<JobSlotState, JobSlot> QualificationsRequiredAnewActive() =>
        DelegateCondition<JobSlotState, JobSlot>.FromContext(QualificationActiveReason, context =>
        {
            var anew = context.Entity.Qualifications.Where(required => !Kept(context, required.QualificationId)).ToList();
            return anew.All(required => required.Qualification is { } qualification && qualification.Id == required.QualificationId && qualification.State == QualificationState.Active);
        }, $"{nameof(JobSlot.Qualifications)}.{nameof(JobSlotQualification.Qualification)}");

    private static DelegateCondition<JobSlotState, JobSlot> ServiceActiveWhenNamed() =>
        DelegateCondition<JobSlotState, JobSlot>.FromContext(ServiceActiveReason, context =>
            context.Entity.ServiceId is not { } named
            || (context.ServiceProvider?.GetService(typeof(IJobSlotGuard)) is IJobSlotGuard guard && guard.StoredService(context.Entity) == named)
            || (context.Entity.Service is { } service && service.Id == named && service.State == ServiceState.Active),
            nameof(JobSlot.Service));

    private static bool Kept(WorkflowContext<JobSlotState, JobSlot> context, Guid qualificationId) =>
        context.ServiceProvider?.GetService(typeof(IJobSlotGuard)) is IJobSlotGuard guard && guard.StoredQualifications(context.Entity).Contains(qualificationId);

    private static DelegateCondition<JobSlotState, JobSlot> InOwnTenant() =>
        DelegateCondition<JobSlotState, JobSlot>.FromContext(JobWorkflowAttribute.OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);
}
