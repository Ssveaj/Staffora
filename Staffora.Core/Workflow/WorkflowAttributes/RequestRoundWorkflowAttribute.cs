using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Jobs;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class RequestRoundWorkflowAttribute : WorkflowAttribute
{
    public const string SendStep = "round-send";

    public const string CloseStep = "round-close";

    public const string FilledStep = "round-filled";

    public const string ExpireStep = "round-expire";

    public const string UnansweredReason = "A request of the round is not answered yet";

    public const string FirstToAcceptRule = "The first to accept gets a slot.";

    public const string CoordinatorPicksRule = "The coordinator picks among those who accept.";

    public const string CloseConfirmation = "Close this round? Its unanswered requests are canceled and their staff told, but yeses waiting for your pick stay.";

    public static string RuleOf(bool firstToAccept) => firstToAccept ? FirstToAcceptRule : CoordinatorPicksRule;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<RequestRoundState, RequestRound>();

        foreach (var state in Enum.GetValues<RequestRoundState>())
        {
            var configured = workflow.AddState(state).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));

            if (state == RequestRoundState.Closed)
            {
                configured.Cascade(e => e.Requests, SlotAssignmentState.Canceled, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned, SlotAssignmentState.Declined, SlotAssignmentState.NotSelected, SlotAssignmentState.Expired);
            }

            if (state == RequestRoundState.Expired)
            {
                configured.Cascade(e => e.Requests, SlotAssignmentState.Expired, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned, SlotAssignmentState.Declined, SlotAssignmentState.NotSelected, SlotAssignmentState.Canceled);
            }
        }

        workflow.AddTransition(new(SendStep, RequestRoundState.Create, RequestRoundState.Open)
        {
            Label = "Send round",
            Description = "Send a request to each staff member chosen, for any open slot of the slot group",
            Icon = "campaign",
            RequiredPermission = RolePermission.PerformCoordination,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(StaffingOpen())
        .AddCondition(SlotActive())
        .AddCondition(GroupOpen())
        .AddCondition(DelegateCondition<RequestRoundState, RequestRound>.FromContext(SlotAssignmentWorkflowAttribute.RespondByAheadReason, context => context.Entity.RespondBy is not { } by || by > context.TimeProvider.GetUtcNow().UtcDateTime));

        workflow.AddTransition(new(CloseStep, RequestRoundState.Open, RequestRoundState.Closed)
        {
            Label = "Close round",
            Description = "Close the round: its unanswered requests are canceled, but yeses waiting for a pick stay",
            Icon = "cancel",
            ConfirmationMessage = CloseConfirmation,
            RequiredPermission = RolePermission.PerformCoordination,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
            TakesNoEdits = true,
        });

        workflow.AddTransition(new(FilledStep, RequestRoundState.Open, RequestRoundState.Filled)
        {
            Label = "Filled",
            Description = "Every slot of the round's slot group got a holder",
            Icon = "task_alt",
            IsSystemDriven = true,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddAction(FilledNotice());

        workflow.AddTransition(new(ExpireStep, RequestRoundState.Open, RequestRoundState.Expired)
        {
            Label = "Expired",
            Description = "The answer-by time passed: the round's unanswered requests expired together and the coordinator was told, while yeses waiting for a pick stay",
            Icon = "timer_off",
            IsSystemDriven = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
            TakesNoEdits = true,
        })
        .AddCondition(DelegateCondition<RequestRoundState, RequestRound>.FromContext(SlotAssignmentWorkflowAttribute.RespondByPassedReason, context => SlotAssignmentWorkflowAttribute.IsPast(context.Entity.RespondBy, context.TimeProvider.GetUtcNow().UtcDateTime)))
        .AddCondition(new DelegateCondition<RequestRoundState, RequestRound>(UnansweredReason, e => e.Requests.Any(request => SlotAssignmentWorkflowAttribute.IsAsked(request.State)), nameof(RequestRound.Requests)))
        .AddAction(ExpiredNotice());

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    private static DelegateCondition<RequestRoundState, RequestRound> StaffingOpen() =>
        new(SlotAssignmentWorkflowAttribute.StaffingOpenReason, e => e.Slot is { } slot && slot.Id == e.JobSlotId && slot.Job is { } job && job.Id == slot.JobId && JobRules.StaffingOpen(job.State),
            nameof(RequestRound.Slot), $"{nameof(RequestRound.Slot)}.{nameof(JobSlot.Job)}");

    private static DelegateCondition<RequestRoundState, RequestRound> SlotActive() =>
        new(SlotAssignmentWorkflowAttribute.SlotActiveReason, e => e.Slot is { } slot && slot.Id == e.JobSlotId && slot.State == JobSlotState.Active, nameof(RequestRound.Slot));

    private static DelegateCondition<RequestRoundState, RequestRound> GroupOpen() =>
        new(SlotAssignmentWorkflowAttribute.GroupOpenReason, e => e.Slot is { } slot && SlotAssignmentWorkflowAttribute.GroupSlotsOf(slot) is { Count: > 0 } group
                && group.SelectMany(member => member.Assignments).DistinctBy(assignment => assignment.Id).Count(assignment => assignment.State == SlotAssignmentState.Assigned) < group.Count,
            $"{nameof(RequestRound.Slot)}.{nameof(JobSlot.Job)}.{nameof(Job.Slots)}.{nameof(JobSlot.Qualifications)}",
            $"{nameof(RequestRound.Slot)}.{nameof(JobSlot.Job)}.{nameof(Job.Slots)}.{nameof(JobSlot.Assignments)}");

    private static NotifyAction<RequestRoundState, RequestRound> FilledNotice() =>
        new(MessageTypes.RoundFilled, round => new Dictionary<string, string?>
        {
            ["Job"] = round.Slot?.Job?.Name,
            ["Slot"] = round.Slot?.Name,
            ["Names"] = round.Slot is { } slot
                ? string.Join(", ", SlotAssignmentWorkflowAttribute.GroupSlotsOf(slot).SelectMany(member => member.Assignments).DistinctBy(assignment => assignment.Id)
                    .Where(assignment => assignment.State == SlotAssignmentState.Assigned)
                    .Select(assignment => assignment.Resource?.User is { } user ? $"{user.FirstName} {user.LastName}".Trim() : "A staff member")
                    .Order(StringComparer.CurrentCultureIgnoreCase))
                : null,
            ["RoundId"] = round.Id.ToString(),
        },
            recipients: round => [round.CreatedById],
            namedRecipientsHold: RolePermission.ViewCoordination);

    private static NotifyAction<RequestRoundState, RequestRound> ExpiredNotice() =>
        new(MessageTypes.RoundExpired, round => new Dictionary<string, string?>
        {
            ["Job"] = round.Slot?.Job?.Name,
            ["Slot"] = round.Slot?.Name,
            ["Names"] = string.Join(", ", round.Requests
                .Where(request => SlotAssignmentWorkflowAttribute.IsAsked(request.State) || request.State == SlotAssignmentState.Expired)
                .Select(request => request.Resource?.User is { } user ? $"{user.FirstName} {user.LastName}".Trim() : "A staff member")
                .Order(StringComparer.CurrentCultureIgnoreCase)),
            ["RoundId"] = round.Id.ToString(),
        },
            recipients: round => new[] { round.CreatedById, round.Slot?.Job?.CoordinatorUserId }.OfType<Guid>(),
            namedRecipientsHold: RolePermission.ViewCoordination);

    private static DelegateCondition<RequestRoundState, RequestRound> InOwnTenant() =>
        DelegateCondition<RequestRoundState, RequestRound>.FromContext(JobWorkflowAttribute.OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);
}
