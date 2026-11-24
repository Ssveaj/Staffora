using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class WorkPatternWorkflowAttribute : WorkflowAttribute
{
    public const string AreaSegments = "Segments";

    public const string SaveStep = "pattern-save";

    public const string PublishStep = "pattern-publish";

    public const string ArchiveStep = "pattern-archive";

    public const string DeleteStep = "pattern-delete";

    public const string HasSegmentsReason = "The pattern has at least one segment of working hours";

    public const string SegmentsValidReason = "Each segment is on a day of the cycle, starts before it ends, and overlaps no other segment of its day";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<WorkPatternState, WorkPattern>();

        workflow.AddState(WorkPatternState.Draft)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Name, FieldAccessFlags.Required)
                .Field(e => e.Description, FieldAccessFlags.ReadWrite)
                .Field(e => e.CycleWeeks, FieldAccessFlags.ReadWrite)
                .Area(AreaSegments, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Segments)));

        foreach (var kept in new[] { WorkPatternState.Published, WorkPatternState.Archived, WorkPatternState.Deleted })
        {
            workflow.AddState(kept)
                .ConfigureFields(f => f
                    .Default(FieldAccessFlags.ReadOnly)
                    .Area(AreaSegments, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Segments)));
        }

        workflow.AddTransition(new(SaveStep, WorkPatternState.Draft, WorkPatternState.Draft)
        {
            Label = "Save",
            Description = "Save the draft's name, cycle and working hours. It stays a draft, assigned to nobody",
            Icon = "save",
            RequiredPermission = RolePermission.ManageWorkPatterns,
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<WorkPatternState, WorkPattern>(e => e.Name))
        .AddCondition(new SegmentsValid());

        workflow.AddTransition(new(PublishStep, WorkPatternState.Draft, WorkPatternState.Published)
        {
            Label = "Publish",
            Description = "Make the pattern ready for use: it can be assigned to staff members and no longer changes",
            Icon = "event_available",
            ConfirmationMessage = "Publish this pattern? It can be assigned to staff members and can no longer be changed. To change it later, copy it to a new draft.",
            RequiredPermission = RolePermission.ManageWorkPatterns,
            ShowWhenBlocked = true,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<WorkPatternState, WorkPattern>(e => e.Name))
        .AddCondition(new DelegateCondition<WorkPatternState, WorkPattern>(HasSegmentsReason, e => e.Segments.Count > 0, nameof(WorkPattern.Segments)))
        .AddCondition(new SegmentsValid());

        workflow.AddTransition(new(ArchiveStep, WorkPatternState.Published, WorkPatternState.Archived)
        {
            Label = "Archive",
            Description = "Take the pattern out of use: it is assigned to nobody new, but staff members who have it keep it",
            Icon = "archive",
            ConfirmationMessage = "Archive this pattern? Staff who have it keep it as it is. It is assigned to nobody new and cannot be published again. Copy it to a new draft to use its hours again.",
            RequiredPermission = RolePermission.ManageWorkPatterns,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(DeleteStep, WorkPatternState.Draft, WorkPatternState.Deleted)
        {
            Label = "Delete",
            Description = "Remove the draft with its working hours, but its history is kept. Only a draft, which nobody has, is deleted",
            Icon = "delete",
            ConfirmationMessage = "Delete this draft? It is removed for good with its working hours, but its history is kept.",
            RequiredPermission = RolePermission.ManageWorkPatterns,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        return workflow;
    }

    private sealed class SegmentsValid : Condition<WorkPatternState, WorkPattern>
    {
        public SegmentsValid() : base(SegmentsValidReason, [nameof(WorkPattern.Segments)])
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<WorkPatternState, WorkPattern> context)
        {
            var reasons = base.GetBlockingReasons(context);
            return reasons.Count > 0 && reasons[0] == Description ? WorkPatternRules.Problems(context.Entity) : reasons;
        }

        protected override bool IsMet(WorkflowContext<WorkPatternState, WorkPattern> context) => WorkPatternRules.Problems(context.Entity).Count == 0;
    }
}
