using Staffora.Core.Jobs;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class SlotAssignmentWorkflowAttribute : WorkflowAttribute
{
    public const string RequestStep = "assignment-request";

    public const string AssignStep = "assignment-assign";

    public const string AcceptStep = "assignment-accept";

    public const string DeclineStep = "assignment-decline";

    public const string WithdrawStep = "assignment-withdraw";

    public const string PickStep = "assignment-pick";

    public const string CloseRequestStep = "assignment-close-request";

    public const string NotSelectedStep = "assignment-not-selected";

    public const string CancelRequestStep = "assignment-cancel-request";

    public const string CancelAcceptedStep = "assignment-cancel-accepted";

    public const string CancelAssignedStep = "assignment-cancel";

    public const string AskCoordinatorStep = "assignment-ask-coordinator";

    public const string AskCoordinatorAgainStep = "assignment-ask-coordinator-again";

    public const string AnswerStaffStep = "assignment-answer-staff";

    public const string QuestionHandledStep = "assignment-question-handled";

    public const string AcceptedMessageHandledStep = "assignment-accepted-message-handled";

    public const string AssignedMessageHandledStep = "assignment-assigned-message-handled";

    public const string AcceptQuestionStep = "assignment-accept-question";

    public const string DeclineQuestionStep = "assignment-decline-question";

    public const string CloseQuestionStep = "assignment-close-question";

    public const string CancelQuestionStep = "assignment-cancel-question";

    public const string ClaimStep = "assignment-claim";

    public const string ExpireStep = "assignment-expire";

    public const string ExpireQuestionStep = "assignment-expire-question";

    public const string TakeOffStep = "assignment-take-off";

    public const string TakeOffAcceptedStep = "assignment-take-off-accepted";

    public const string TakeOffRequestStep = "assignment-take-off-request";

    public const string TakeOffQuestionStep = "assignment-take-off-question";

    public static readonly IReadOnlyList<string> TakeOffSteps = [TakeOffStep, TakeOffAcceptedStep, TakeOffRequestStep, TakeOffQuestionStep];

    public const string RoundOpenReason = "A request of a round is sent while the round is open, for the slot it was sent from";

    public const string OwnReason = "Only the staff member asked answers the request";

    public const string OwnQuestionReason = "Only the staff member asked writes a question about the request";

    public const string NotOwnQuestionReason = "A coordinator does not answer a question about their own request";

    public const string AwaitsReplyReason = "The staff member's last message waits for a reply";

    public const string StaffingOpenReason = "The job is being staffed (coordinating, confirmed or in progress)";

    public const string SlotActiveReason = "The slot is active";

    public const string StaffInServiceReason = "The staff member is in service (their staff record and their account are active)";

    public const string GroupOpenReason = "Only a slot group with an open slot takes another: cancel an assigned staff member first to staff it anew";

    public const string OneHolderReason = GroupOpenReason;

    public const string StaffFreeReason = "The staff member is free for every remaining occurrence of the job: not absent, not out of service and not booked on another job at the same time";

    public const string OneLiveReason = "The staff member has no open request or assignment for this slot group already";

    public const string RespondByAheadReason = "The answer-by time lies ahead, or there is none";

    public const string RespondByPassedReason = "The answer-by time passed";

    public const string AssignAsksNoAnswerReason = "An assignment made straight away asks for no answer, so it has no answer-by time";

    public const string RoundTimeReason = "A request of a round answers by the round's answer-by time";

    public const string AwayReason = "An absence of the staff member blocks a remaining time of the job";

    public static readonly TransitionCommentPrompt DeclinePrompt = new("Decline the request")
    {
        Message = "Why do you decline? The coordinator reads your reason in this assignment's conversation.",
        Placeholder = "The reason",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt WithdrawPrompt = new("Take back your yes")
    {
        Message = "Why do you take back your yes? The coordinator reads your reason in this assignment's conversation.",
        Placeholder = "The reason",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AskCoordinatorPrompt = new("Write a question to the coordinator")
    {
        Message = "The coordinator sees your question on the request.",
        Placeholder = "What do you want to ask about?",
        RequiredMessage = "Write the question first, or the coordinator only sees that something happened.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AskCoordinatorAgainPrompt = new("Write another question")
    {
        Message = "The request stays with the coordinator with your new question.",
        Placeholder = "What else do you want to ask about?",
        RequiredMessage = "Write the question first, or the coordinator sees nothing new.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AnswerStaffPrompt = new("Answer the staff member")
    {
        Message = "The request goes back to the staff member with your answer.",
        Placeholder = "Your answer",
        RequiredMessage = "Write the answer first, or the staff member only sees that something happened.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt TakeOffPrompt = new("Take off for absence")
    {
        Message = "The staff member is told that they no longer work this slot because of their absence, and the slot is open again for someone else. Add a word for them if you like.",
        Placeholder = "A word for the staff member (optional)",
        Audience = CommentAudience.Staff,
        Required = false,
    };

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<SlotAssignmentState, SlotAssignment>();

        foreach (var state in Enum.GetValues<SlotAssignmentState>())
        {
            workflow.AddState(state).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        }

        workflow.AddTransition(new(RequestStep, SlotAssignmentState.Create, SlotAssignmentState.Requested)
        {
            Label = "Request",
            Description = "Ask the staff member to work for this slot. They accept or decline",
            Icon = "forward_to_inbox",
            RequiredPermission = RolePermission.PerformCoordination,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(StaffingOpen())
        .AddCondition(SlotActive())
        .AddCondition(StaffInService())
        .AddCondition(OneLive())
        .AddCondition(GroupOpen())
        .AddCondition(new StaffFree())
        .AddCondition(RoundOpen())
        .AddCondition(RespondByAhead())
        .AddCondition(RoundTime())
        .AddAction(StaffNotice(MessageTypes.AssignmentRequested, when: context => context.Entity.RequestRoundId is null))
        .AddAction(StaffNotice(MessageTypes.AssignmentRequestedInRound, when: context => context.Entity.RequestRoundId is not null));

        workflow.AddTransition(new(AssignStep, SlotAssignmentState.Create, SlotAssignmentState.Assigned)
        {
            Label = "Assign",
            Description = "Assign the staff member to this slot straight away. Other open requests for it are closed",
            Icon = "how_to_reg",
            RequiredPermission = RolePermission.PerformCoordination,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(StaffingOpen())
        .AddCondition(SlotActive())
        .AddCondition(StaffInService())
        .AddCondition(OneLive())
        .AddCondition(GroupOpen())
        .AddCondition(new StaffFree())
        .AddCondition(AsksNoAnswer())
        .AddAction(TakeOpenSlot())
        .AddAction(StaffNotice(MessageTypes.AssignmentAssigned))
        .AddAction(TellThePortalOfStaffMembers());

        foreach (var (key, from) in new[] { (AcceptStep, SlotAssignmentState.Requested), (AcceptQuestionStep, SlotAssignmentState.StaffQuestion) })
        {
            workflow.AddTransition(new(key, from, SlotAssignmentState.Accepted)
            {
                Label = "Accept",
                Description = "Say yes: the coordinator then picks who works",
                Icon = "thumb_up",
                RequiredPermission = RolePermission.RespondToAssignment,
                ShowWhenBlocked = true,
                SortOrder = 30,
                TakesNoEdits = true,
            })
            .AddCondition(Own())
            .AddCondition(StaffingOpen())
            .AddCondition(SlotActive())
            .AddCondition(StaffInService())
            .AddCondition(GroupOpen())
            .AddCondition(new StaffFree())
            .AddAction(CoordinatorNotice(MessageTypes.AssignmentAccepted, when: context => !TakesASlotAtOnce(context.Entity)));
        }

        foreach (var (key, from) in new[] { (DeclineStep, SlotAssignmentState.Requested), (DeclineQuestionStep, SlotAssignmentState.StaffQuestion) })
        {
            workflow.AddTransition(new(key, from, SlotAssignmentState.Declined)
            {
                Label = "Decline",
                Description = "Say no, with your reason for the coordinator",
                Icon = "thumb_down",
                RequiredPermission = RolePermission.RespondToAssignment,
                CommentPrompt = DeclinePrompt,
                SortOrder = 10,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            })
            .AddCondition(Own())
            .AddCondition(StaffingOpen())
            .AddAction(CoordinatorNotice(MessageTypes.AssignmentDeclined));
        }

        AddQuestionSteps(workflow);

        workflow.AddTransition(new(WithdrawStep, SlotAssignmentState.Accepted, SlotAssignmentState.Declined)
        {
            Label = "Take back my yes",
            Description = "Take back your yes before the coordinator picks, with your reason for the coordinator",
            Icon = "undo",
            RequiredPermission = RolePermission.RespondToAssignment,
            CommentPrompt = WithdrawPrompt,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
            TakesNoEdits = true,
        })
        .AddCondition(Own())
        .AddCondition(StaffingOpen())
        .AddAction(CoordinatorNotice(MessageTypes.AssignmentWithdrawn));

        workflow.AddTransition(new(PickStep, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned)
        {
            Label = "Assign",
            Description = "Pick this staff member, who said yes. The slot's other open requests are closed as not selected",
            Icon = "how_to_reg",
            RequiredPermission = RolePermission.PerformCoordination,
            ShowWhenBlocked = true,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(StaffingOpen())
        .AddCondition(SlotActive())
        .AddCondition(StaffInService())
        .AddCondition(GroupOpen())
        .AddCondition(new StaffFree())
        .AddAction(TakeOpenSlot())
        .AddAction(StaffNotice(MessageTypes.AssignmentAssigned))
        .AddAction(TellThePortalOfStaffMembers());

        workflow.AddTransition(new(ClaimStep, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned)
        {
            Label = "Took a slot",
            Description = "The staff member's yes to a first-to-accept round took an open slot of the slot group",
            Icon = "how_to_reg",
            IsSystemDriven = true,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(StaffingOpen())
        .AddCondition(SlotActive())
        .AddCondition(GroupOpen())
        .AddAction(TakeOpenSlot())
        .AddAction(StaffNotice(MessageTypes.AssignmentAssigned))
        .AddAction(CoordinatorNotice(MessageTypes.AssignmentTaken))
        .AddAction(TellThePortalOfStaffMembers());

        foreach (var (key, from) in new[] { (CloseRequestStep, SlotAssignmentState.Requested), (CloseQuestionStep, SlotAssignmentState.StaffQuestion), (NotSelectedStep, SlotAssignmentState.Accepted) })
        {
            workflow.AddTransition(new(key, from, SlotAssignmentState.NotSelected)
            {
                Label = "Not selected",
                Description = "Another staff member was assigned to the slot, or the job's period ended before this staff member was picked",
                Icon = "person_remove",
                IsSystemDriven = true,
                SortOrder = 10,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            })
            .AddAction(StaffNotice(MessageTypes.AssignmentNotSelected, when: StaffingStillOpen));
        }

        foreach (var (key, from) in new[] { (ExpireStep, SlotAssignmentState.Requested), (ExpireQuestionStep, SlotAssignmentState.StaffQuestion) })
        {
            workflow.AddTransition(new(key, from, SlotAssignmentState.Expired)
            {
                Label = "Expired",
                Description = "Nobody answered before the answer-by time, so the request closed by itself and the coordinator was told",
                Icon = "timer_off",
                IsSystemDriven = true,
                SortOrder = 10,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            })
            .AddCondition(RespondByPassed())
            .AddAction(CoordinatorNotice(MessageTypes.AssignmentExpired, when: context => !(context.Entity.Round is { } round && context.IsTakenWith(round))));
        }

        foreach (var (key, from, label, confirmation) in new[]
        {
            (CancelRequestStep, SlotAssignmentState.Requested, "Withdraw request", "Withdraw this request? The staff member is told, and its history is kept."),
            (CancelQuestionStep, SlotAssignmentState.StaffQuestion, "Withdraw request", "Withdraw this request? Its question lapses, the staff member is told, and its history is kept."),
            (CancelAcceptedStep, SlotAssignmentState.Accepted, "Cancel", "Close this yes without assigning the staff member? The staff member is told, and its history is kept."),
            (CancelAssignedStep, SlotAssignmentState.Assigned, "Cancel assignment", "Cancel this assignment? The staff member is told, and the slot has no staff member until another is assigned."),
        })
        {
            var cancel = workflow.AddTransition(new(key, from, SlotAssignmentState.Canceled)
            {
                Label = label,
                Description = "Cancel it: the staff member no longer works or is asked for this slot, but its history is kept",
                Icon = "cancel",
                ConfirmationMessage = confirmation,
                RequiredPermission = RolePermission.PerformCoordination,
                SortOrder = 10,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            })
            .AddCondition(StaffingOpen())
            .AddAction(StaffNotice(MessageTypes.AssignmentCanceled));

            if (from == SlotAssignmentState.Assigned)
            {
                cancel.AddAction(TellThePortalOfStaffMembers());
            }
        }

        foreach (var (key, from) in new[] { (TakeOffRequestStep, SlotAssignmentState.Requested), (TakeOffQuestionStep, SlotAssignmentState.StaffQuestion), (TakeOffAcceptedStep, SlotAssignmentState.Accepted), (TakeOffStep, SlotAssignmentState.Assigned) })
        {
            var takeOff = workflow.AddTransition(new(key, from, SlotAssignmentState.Canceled)
            {
                Label = "Take off for absence",
                Description = "The staff member is absent for a time of the job: take them off it, tell them why, and open the slot again for someone else",
                Icon = "event_busy",
                RequiredPermission = RolePermission.PerformCoordination,
                CommentPrompt = TakeOffPrompt,
                SortOrder = 20,
                TakesNoEdits = true,
                NeverCascaded = true,
            })
            .AddCondition(StaffingOpen())
            .AddCondition(new Away())
            .AddAction(TakenOffNotice());

            if (from == SlotAssignmentState.Assigned)
            {
                takeOff.AddAction(TellThePortalOfStaffMembers());
            }
        }

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    private static void AddQuestionSteps(Workflow<SlotAssignmentState, SlotAssignment> workflow)
    {
        workflow.AddTransition(new(AskCoordinatorStep, SlotAssignmentState.Requested, SlotAssignmentState.StaffQuestion)
        {
            Label = "Write a question",
            Description = "A window opens where you write your question. The coordinator answers before you accept or decline",
            Icon = "contact_support",
            RequiredPermission = RolePermission.RespondToAssignment,
            CommentPrompt = AskCoordinatorPrompt,
            SortOrder = 20,
            TakesNoEdits = true,
        })
        .AddCondition(Own(OwnQuestionReason))
        .AddCondition(StaffingOpen())
        .AddAction(CoordinatorNotice(MessageTypes.AssignmentQuestionFromStaff, withComment: true, fallback: RolePermission.PerformCoordination));

        workflow.AddTransition(new(AskCoordinatorAgainStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.StaffQuestion)
        {
            Label = "Write another question",
            Description = "The coordinator gets one more question, and the request stays with them",
            Icon = "contact_support",
            RequiredPermission = RolePermission.RespondToAssignment,
            CommentPrompt = AskCoordinatorAgainPrompt,
            SortOrder = 20,
            TakesNoEdits = true,
        })
        .AddCondition(Own(OwnQuestionReason))
        .AddCondition(StaffingOpen())
        .AddAction(CoordinatorNotice(MessageTypes.AssignmentQuestionFromStaff, withComment: true, fallback: RolePermission.PerformCoordination));

        workflow.AddTransition(new(AnswerStaffStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Requested)
        {
            Label = "Answer the staff member",
            Description = "Your answer goes to the staff member, and the request is theirs again to accept or decline",
            Icon = "forum",
            RequiredPermission = RolePermission.PerformCoordination,
            CommentPrompt = AnswerStaffPrompt,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(NotOwn(NotOwnQuestionReason))
        .AddCondition(StaffingOpen())
        .AddAction(StaffNotice(MessageTypes.AssignmentAnswerToStaff, withComment: true))
        .AddAction(SettleStaffNotices());

        workflow.AddTransition(new(QuestionHandledStep, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Requested)
        {
            Label = "Mark as handled",
            Description = "The question needs no written answer. The request is the staff member's again to accept or decline, and nobody is told",
            Icon = "done_all",
            RequiredPermission = RolePermission.PerformCoordination,
            SortOrder = 20,
            TakesNoEdits = true,
        })
        .AddCondition(NotOwn(NotOwnQuestionReason))
        .AddCondition(StaffingOpen())
        .AddAction(SettleStaffNotices());

        foreach (var (key, state) in new[] { (AcceptedMessageHandledStep, SlotAssignmentState.Accepted), (AssignedMessageHandledStep, SlotAssignmentState.Assigned) })
        {
            workflow.AddTransition(new(key, state, state)
            {
                Label = "Mark as handled",
                Description = "The staff member's message needs no written reply. It no longer waits for one, and nobody is told",
                Icon = "done_all",
                RequiredPermission = RolePermission.PerformCoordination,
                SortOrder = 20,
                TakesNoEdits = true,
            })
            .AddCondition(AwaitsReply())
            .AddCondition(NotOwn(NotOwnQuestionReason))
            .AddCondition(StaffingOpen())
            .AddAction(StampHandled())
            .AddAction(SettleStaffNotices());
        }
    }

    public static readonly IReadOnlyList<MessageType> StaffNoticeTypes = [MessageTypes.AssignmentQuestionFromStaff, MessageTypes.AssignmentStaffWrote];

    private static MarkNoticesReadAction<SlotAssignmentState, SlotAssignment> SettleStaffNotices() => new(StaffNoticeTypes);

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> AwaitsReply() =>
        new(AwaitsReplyReason, e => StaffConversation.AwaitsCoordinatorReply(e.LastStaffCommentAt, e.LastCoordinatorCommentAt));

    private static DelegateAction<SlotAssignmentState, SlotAssignment> StampHandled() => new(context =>
    {
        var assignment = context.Entity;
        var now = context.TimeProvider.GetUtcNow().UtcDateTime;
        var unseen = StaffConversation.HasUnseenReply(assignment.LastCoordinatorCommentAt, assignment.StaffSeenAt);
        var handled = assignment.LastStaffCommentAt is { } staff && staff >= now ? staff.AddMilliseconds(1) : now;
        assignment.LastCoordinatorCommentAt = handled;
        if (!unseen)
        {
            assignment.StaffSeenAt = assignment.StaffSeenAt is { } seen && seen > handled ? seen : handled;
        }
    });

    public static bool IsLive(SlotAssignmentState state) => state is SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion or SlotAssignmentState.Accepted or SlotAssignmentState.Assigned;

    public static bool IsTakeOff(string? transitionKey) => transitionKey is TakeOffStep or TakeOffAcceptedStep or TakeOffRequestStep or TakeOffQuestionStep;

    public static bool IsAsked(SlotAssignmentState state) => state is SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion;

    public static bool IsAcceptStep(string? transitionKey) => transitionKey is AcceptStep or AcceptQuestionStep;

    public static string? NotSelectedStepFor(SlotAssignmentState state) => state switch
    {
        SlotAssignmentState.Requested => CloseRequestStep,
        SlotAssignmentState.StaffQuestion => CloseQuestionStep,
        SlotAssignmentState.Accepted => NotSelectedStep,
        _ => null,
    };

    public static bool Holds(SlotAssignmentState state) => state is SlotAssignmentState.Accepted or SlotAssignmentState.Assigned;

    private static bool IsSelf(SlotAssignment other, SlotAssignment assignment) => ReferenceEquals(other, assignment) || other.Id == assignment.Id;

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> Own(string reason = OwnReason) =>
        DelegateCondition<SlotAssignmentState, SlotAssignment>.FromContext(reason, context => IsOwn(context) == true, nameof(SlotAssignment.Resource));

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> NotOwn(string reason) =>
        DelegateCondition<SlotAssignmentState, SlotAssignment>.FromContext(reason, context => IsOwn(context) == false, nameof(SlotAssignment.Resource));

    private static bool? IsOwn(WorkflowContext<SlotAssignmentState, SlotAssignment> context) =>
        context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { EffectiveUserId: { } actor }
        && context.Entity.Resource is { } resource && resource.Id == context.Entity.ResourceId
            ? resource.UserId == actor
            : null;

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> StaffingOpen() =>
        new(StaffingOpenReason, e => e.Slot is { } slot && slot.Id == e.JobSlotId && slot.Job is { } job && job.Id == slot.JobId && JobRules.StaffingOpen(job.State),
            nameof(SlotAssignment.Slot), $"{nameof(SlotAssignment.Slot)}.{nameof(JobSlot.Job)}");

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> SlotActive() =>
        new(SlotActiveReason, e => e.Slot is { } slot && slot.Id == e.JobSlotId && slot.State == JobSlotState.Active, nameof(SlotAssignment.Slot));

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> StaffInService() =>
        new(StaffInServiceReason, e => e.Resource is { } resource && resource.Id == e.ResourceId && StaffServiceRules.IsInService(resource),
            nameof(SlotAssignment.Resource), $"{nameof(SlotAssignment.Resource)}.{nameof(Resource.User)}");

    private static readonly string[] GroupReads =
    [
        $"{nameof(SlotAssignment.Slot)}.{nameof(JobSlot.Assignments)}",
        $"{nameof(SlotAssignment.Slot)}.{nameof(JobSlot.Job)}.{nameof(Job.Slots)}.{nameof(JobSlot.Qualifications)}",
        $"{nameof(SlotAssignment.Slot)}.{nameof(JobSlot.Job)}.{nameof(Job.Slots)}.{nameof(JobSlot.Assignments)}",
    ];

    public static IReadOnlyList<JobSlot> GroupSlotsOf(SlotAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return assignment.Slot is { } own ? GroupSlotsOf(own) : [];
    }

    public static IReadOnlyList<JobSlot> GroupSlotsOf(JobSlot own)
    {
        ArgumentNullException.ThrowIfNull(own);
        var slots = new Dictionary<Guid, JobSlot>();
        foreach (var slot in own.Job?.Slots ?? [])
        {
            slots.TryAdd(slot.Id, slot);
        }

        slots[own.Id] = own;
        return SlotGroups.GroupOf(own.Id, slots.Values.Select(SlotGroupInput.Of)) is { } group ? [.. group.SlotIds.Select(id => slots[id])] : [own];
    }

    private static IEnumerable<SlotAssignment> OthersIn(IEnumerable<JobSlot> slots, SlotAssignment assignment) =>
        slots.SelectMany(slot => slot.Assignments).Where(other => !IsSelf(other, assignment)).DistinctBy(other => other.Id == Guid.Empty ? (object)other : other.Id);

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> GroupOpen() =>
        new(GroupOpenReason, e => e.Slot is not null && GroupSlotsOf(e) is { Count: > 0 } group && OthersIn(group, e).Count(other => other.State == SlotAssignmentState.Assigned) < group.Count,
            GroupReads);

    public static bool TakesASlotAtOnce(SlotAssignment assignment) =>
        assignment.Round is { State: RequestRoundState.Open, FirstToAccept: true } round && round.Id == assignment.RequestRoundId;

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> RoundOpen() =>
        new(RoundOpenReason, e => e.RequestRoundId is null || (e.Round is { State: RequestRoundState.Open } round && round.Id == e.RequestRoundId && round.JobSlotId == e.JobSlotId),
            nameof(SlotAssignment.Round));

    public static string? ExpireStepFor(SlotAssignmentState state) => state switch
    {
        SlotAssignmentState.Requested => ExpireStep,
        SlotAssignmentState.StaffQuestion => ExpireQuestionStep,
        _ => null,
    };

    public static bool IsPast(DateTime? respondBy, DateTime nowUtc) => respondBy is { } by && by <= nowUtc;

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> RespondByAhead() =>
        DelegateCondition<SlotAssignmentState, SlotAssignment>.FromContext(RespondByAheadReason, context => context.Entity.RespondBy is not { } by || by > context.TimeProvider.GetUtcNow().UtcDateTime);

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> RespondByPassed() =>
        DelegateCondition<SlotAssignmentState, SlotAssignment>.FromContext(RespondByPassedReason, context => IsPast(context.Entity.RespondBy, context.TimeProvider.GetUtcNow().UtcDateTime));

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> AsksNoAnswer() =>
        new(AssignAsksNoAnswerReason, e => e.RespondBy is null);

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> RoundTime() =>
        new(RoundTimeReason, e => e.RequestRoundId is null || (e.Round is { } round && round.Id == e.RequestRoundId && round.RespondBy == e.RespondBy),
            nameof(SlotAssignment.Round));

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> OneLive() =>
        new(OneLiveReason, e => e.Slot is not null && !OthersIn(GroupSlotsOf(e), e).Any(other => other.ResourceId == e.ResourceId && IsLive(other.State)),
            GroupReads);

    private static DelegateAction<SlotAssignmentState, SlotAssignment> TakeOpenSlot() => new(context =>
    {
        var assignment = context.Entity;
        var group = GroupSlotsOf(assignment);
        var held = group.Where(slot => slot.Assignments.Any(other => !IsSelf(other, assignment) && other.State == SlotAssignmentState.Assigned)).Select(slot => slot.Id).ToHashSet();
        var open = SlotGroups.FirstOpen(new SlotGroup(default, [.. group.Select(slot => slot.Id)]), assignment.JobSlotId, held)
            ?? throw new InvalidOperationException($"Every slot of the group of slot {assignment.JobSlotId} is held, so assignment {assignment.Id} takes none.");
        if (open != assignment.JobSlotId)
        {
            var slot = group.First(member => member.Id == open);
            assignment.Slot = slot;
            assignment.JobSlotId = slot.Id;
        }
    });

    private sealed class StaffFree : Condition<SlotAssignmentState, SlotAssignment>
    {
        private const string NoGuard = $"{StaffFreeReason} (cannot be checked: no availability guard)";

        public StaffFree() : base(StaffFreeReason)
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<SlotAssignmentState, SlotAssignment> context) =>
            context.ServiceProvider?.GetService(typeof(ISlotAssignmentGuard)) is not ISlotAssignmentGuard guard
                ? [NoGuard]
                : guard.VerdictOf(context.Entity).BlockedText is { } blocked ? [blocked] : [];

        protected override bool IsMet(WorkflowContext<SlotAssignmentState, SlotAssignment> context) => GetBlockingReasons(context).Count == 0;
    }

    private sealed class Away : Condition<SlotAssignmentState, SlotAssignment>
    {
        private const string NoGuard = $"{AwayReason} (cannot be checked: no availability guard)";

        public Away() : base(AwayReason)
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<SlotAssignmentState, SlotAssignment> context) =>
            context.ServiceProvider?.GetService(typeof(ISlotAssignmentGuard)) is not ISlotAssignmentGuard guard
                ? [NoGuard]
                : guard.BlockingAbsenceOf(context.Entity) is null ? [AwayReason] : [];

        protected override bool IsMet(WorkflowContext<SlotAssignmentState, SlotAssignment> context) => GetBlockingReasons(context).Count == 0;
    }

    public static string AbsenceText(ScheduledAbsence absence)
    {
        ArgumentNullException.ThrowIfNull(absence);
        return $"{AbsenceRules.KindText(absence.Kind)} {AbsenceRules.PeriodText(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime)}";
    }

    private static NotifyAction<SlotAssignmentState, SlotAssignment> TakenOffNotice() =>
        NotifyAction<SlotAssignmentState, SlotAssignment>.FromContext(MessageTypes.AssignmentTakenOff, context =>
            {
                var assignment = context.Entity;
                var absence = context.ServiceProvider?.GetService(typeof(ISlotAssignmentGuard)) is ISlotAssignmentGuard guard ? guard.BlockingAbsenceOf(assignment) : null;
                return new Dictionary<string, string?>
                {
                    ["FirstName"] = assignment.Resource?.User?.FirstName,
                    ["Job"] = JobName(assignment),
                    ["Slot"] = assignment.Slot?.Name,
                    ["Absence"] = absence is null ? "absence" : AbsenceText(absence),
                    ["AssignmentId"] = assignment.Id.ToString(),
                };
            },
            recipients: assignment => assignment.Resource is { } resource
                ? [resource.UserId]
                : throw new InvalidOperationException($"The staff member of the assignment {assignment.Id} was not loaded with it, so the notice \"{MessageTypes.AssignmentTakenOff.Key}\" would reach nobody."),
            withComment: true);

    private static DelegateAction<SlotAssignmentState, SlotAssignment> TellThePortalOfStaffMembers() => new(context =>
    {
        if (context.Entity.Slot?.Job is not { CrossTenantId: not null })
        {
            return;
        }

        var sync = context.ServiceProvider?.GetService(typeof(IJobStateSync)) as IJobStateSync
            ?? throw new InvalidOperationException("A job a customer's portal sent changes its staff members only where its portal can be told.");
        var assignment = context.Entity;
        context.BeforeSave.Add((store, token) => sync.QueueStaffMembersAsync(store, assignment, token));
    });

    private static DelegateCondition<SlotAssignmentState, SlotAssignment> InOwnTenant() =>
        DelegateCondition<SlotAssignmentState, SlotAssignment>.FromContext(JobWorkflowAttribute.OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static NotifyAction<SlotAssignmentState, SlotAssignment> StaffNotice(MessageType type, Func<WorkflowContext<SlotAssignmentState, SlotAssignment>, bool>? when = null, bool withComment = false) =>
        new(type, assignment => new Dictionary<string, string?>
        {
            ["FirstName"] = assignment.Resource?.User?.FirstName,
            ["Job"] = JobName(assignment),
            ["Slot"] = assignment.Slot?.Name,
            ["Period"] = assignment.Slot?.Job is { } job ? JobRules.WhenText(JobTimes.Of(job)) : null,
            ["Job"] = JobName(assignment),
            ["Slot"] = assignment.Slot?.Name,
            ["Period"] = assignment.Slot?.Job is { } period ? JobRules.WhenText(JobTimes.Of(period)) : null,
            ["Rule"] = assignment.Round is { } round ? RequestRoundWorkflowAttribute.RuleOf(round.FirstToAccept) : null,
            ["AssignmentId"] = assignment.Id.ToString(),
        }.Where(value => type.Placeholders.Contains(value.Key)).ToDictionary(),
            recipients: assignment => assignment.Resource is { } resource
                ? [resource.UserId]
                : throw new InvalidOperationException($"The staff member of the assignment {assignment.Id} was not loaded with it, so the notice \"{type.Key}\" would reach nobody."),
            when: when,
            withComment: withComment);

    private static bool StaffingStillOpen(WorkflowContext<SlotAssignmentState, SlotAssignment> context) =>
        context.Entity.Slot?.Job is { } job && JobRules.StaffingOpen(job.State);

    private static NotifyAction<SlotAssignmentState, SlotAssignment> CoordinatorNotice(MessageType type, Func<WorkflowContext<SlotAssignmentState, SlotAssignment>, bool>? when = null, bool withComment = false, RolePermission? fallback = null) =>
        new(type, assignment => new Dictionary<string, string?>
        {
            ["Staff"] = StaffName(assignment),
            ["Job"] = JobName(assignment),
            ["Slot"] = assignment.Slot?.Name,
            ["Staff"] = StaffName(assignment),
            ["Job"] = JobName(assignment),
            ["Slot"] = assignment.Slot?.Name,
            ["AssignmentId"] = assignment.Id.ToString(),
        }.Where(value => type.Placeholders.Contains(value.Key)).ToDictionary(),
            recipients: assignment => new[] { assignment.Slot?.Job?.CoordinatorUserId, assignment.CreatedById }.OfType<Guid>(),
            namedRecipientsHold: RolePermission.ViewCoordination,
            when: when,
            fallbackPermission: fallback,
            withComment: withComment);

    private static string? JobName(SlotAssignment assignment) => assignment.Slot?.Job?.Name;

    private static string StaffName(SlotAssignment assignment) =>
        assignment.Resource?.User is { } user && $"{user.FirstName} {user.LastName}".Trim() is { Length: > 0 } name ? name : "A staff member";
}
