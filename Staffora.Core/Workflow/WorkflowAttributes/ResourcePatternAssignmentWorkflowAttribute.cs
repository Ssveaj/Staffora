using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class ResourcePatternAssignmentWorkflowAttribute : WorkflowAttribute
{
    public const string SaveStep = "pattern-assignment-save";

    public const string ActivateStep = "pattern-assignment-activate";

    public const string DeleteStep = "pattern-assignment-delete";

    public const string SaveActiveStep = "pattern-assignment-save-active";

    public const string DeactivateStep = "pattern-assignment-deactivate";

    public const string ReactivateStep = "pattern-assignment-reactivate";

    public const string PatternPublishedReason = "The work pattern is published (a draft is given to nobody, an archived pattern to nobody new)";

    public const string DatesValidReason = "The assignment ends on or after the day it starts, and its cycle starts on a Monday";

    public const string StaffSetUpOpenReason = "The staff member's record is not inactive (a staff member being set up or in service)";

    public const string NoOverlapReason = "No other active assignment of the staff member applies on any of its dates";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<ResourcePatternAssignmentState, ResourcePatternAssignment>();

        workflow.AddState(ResourcePatternAssignmentState.Registration)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.WorkPatternId, FieldAccessFlags.Required)
                .Field(e => e.EffectiveFrom, FieldAccessFlags.Required)
                .Field(e => e.EffectiveTo, FieldAccessFlags.ReadWrite)
                .Field(e => e.CycleAnchor, FieldAccessFlags.Required));

        workflow.AddState(ResourcePatternAssignmentState.Active)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.EffectiveTo, FieldAccessFlags.ReadWrite));

        foreach (var kept in new[] { ResourcePatternAssignmentState.Inactive, ResourcePatternAssignmentState.Deleted })
        {
            workflow.AddState(kept).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        }

        workflow.AddTransition(new(SaveStep, ResourcePatternAssignmentState.Registration, ResourcePatternAssignmentState.Registration)
        {
            Label = "Save",
            Description = "Save the assignment's pattern and dates. It gives the staff member no hours until it is activated",
            Icon = "save",
            RequiredPermission = RolePermission.AssignWorkPatterns,
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<ResourcePatternAssignmentState, ResourcePatternAssignment>(e => e.WorkPatternId))
        .AddCondition(PatternPublished())
        .AddCondition(new DatesValid())
        .AddCondition(StaffSetUpOpen());

        workflow.AddTransition(new(ActivateStep, ResourcePatternAssignmentState.Registration, ResourcePatternAssignmentState.Active)
        {
            Label = "Activate",
            Description = "Put the assignment into effect: the staff member works by the pattern on every date from its start to its end",
            Icon = "event_available",
            RequiredPermission = RolePermission.AssignWorkPatterns,
            ShowWhenBlocked = true,
            SortOrder = 30,
        })
        .AddCondition(PatternPublished())
        .AddCondition(new DatesValid())
        .AddCondition(StaffSetUpOpen())
        .AddCondition(new NoOverlap());

        workflow.AddTransition(new(DeleteStep, ResourcePatternAssignmentState.Registration, ResourcePatternAssignmentState.Deleted)
        {
            Label = "Delete",
            Description = "Remove the prepared assignment, but its history is kept. Only one that never took effect is deleted",
            Icon = "delete",
            ConfirmationMessage = "Delete this assignment? It never took effect and is removed for good, but its history is kept.",
            RequiredPermission = RolePermission.AssignWorkPatterns,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(SaveActiveStep, ResourcePatternAssignmentState.Active, ResourcePatternAssignmentState.Active)
        {
            Label = "Save",
            Description = "Save the assignment's end date: the staff member works by the pattern until then",
            Icon = "save",
            RequiredPermission = RolePermission.AssignWorkPatterns,
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new DatesValid())
        .AddCondition(StaffSetUpOpen())
        .AddCondition(new NoOverlap());

        workflow.AddTransition(new(DeactivateStep, ResourcePatternAssignmentState.Active, ResourcePatternAssignmentState.Inactive)
        {
            Label = "Withdraw",
            Description = "Withdraw the assignment: it gives the staff member no hours on any of its dates, but its history is kept. To end it on a day instead, give it an end date",
            Icon = "event_busy",
            ConfirmationMessage = "Withdraw this assignment? The staff member no longer works by it on any of its dates, past ones included, but its history is kept. To end it on a day instead, give it an end date and save.",
            RequiredPermission = RolePermission.AssignWorkPatterns,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(ReactivateStep, ResourcePatternAssignmentState.Inactive, ResourcePatternAssignmentState.Active)
        {
            Label = "Reactivate",
            Description = "Put the withdrawn assignment into effect again, with its pattern and dates",
            Icon = "event_available",
            RequiredPermission = RolePermission.AssignWorkPatterns,
            ShowWhenBlocked = true,
            SortOrder = 30,
        })
        .AddCondition(PatternPublished())
        .AddCondition(new DatesValid())
        .AddCondition(StaffSetUpOpen())
        .AddCondition(new NoOverlap());

        return workflow;
    }

    private static DelegateCondition<ResourcePatternAssignmentState, ResourcePatternAssignment> PatternPublished() =>
        new(PatternPublishedReason, e => e.WorkPattern is { State: WorkPatternState.Published }, nameof(ResourcePatternAssignment.WorkPattern));

    private static DelegateCondition<ResourcePatternAssignmentState, ResourcePatternAssignment> StaffSetUpOpen() =>
        new(StaffSetUpOpenReason, e => e.Resource is { } resource && StaffServiceRules.IsSetUpOpen(resource.State), nameof(ResourcePatternAssignment.Resource));

    private sealed class DatesValid : Condition<ResourcePatternAssignmentState, ResourcePatternAssignment>
    {
        public DatesValid() : base(DatesValidReason)
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<ResourcePatternAssignmentState, ResourcePatternAssignment> context) =>
            ScheduleRules.Problems(context.Entity.EffectiveFrom, context.Entity.EffectiveTo, context.Entity.CycleAnchor);

        protected override bool IsMet(WorkflowContext<ResourcePatternAssignmentState, ResourcePatternAssignment> context) =>
            ScheduleRules.Problems(context.Entity.EffectiveFrom, context.Entity.EffectiveTo, context.Entity.CycleAnchor).Count == 0;
    }

    private sealed class NoOverlap : Condition<ResourcePatternAssignmentState, ResourcePatternAssignment>
    {
        public NoOverlap() : base(NoOverlapReason)
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<ResourcePatternAssignmentState, ResourcePatternAssignment> context)
        {
            var entity = context.Entity;
            return ScheduleRules.OverlapReasons(new ScheduledDates(entity.Id, entity.EffectiveFrom, entity.EffectiveTo), Guard(context).OtherActiveAssignmentsOf(entity));
        }

        protected override bool IsMet(WorkflowContext<ResourcePatternAssignmentState, ResourcePatternAssignment> context) => GetBlockingReasons(context).Count == 0;

        private static IPatternAssignmentGuard Guard(WorkflowContext<ResourcePatternAssignmentState, ResourcePatternAssignment> context) =>
            context.ServiceProvider?.GetService(typeof(IPatternAssignmentGuard)) as IPatternAssignmentGuard
                ?? throw new InvalidOperationException("An assignment's activation is judged only where its schedule guard is registered.");
    }
}
