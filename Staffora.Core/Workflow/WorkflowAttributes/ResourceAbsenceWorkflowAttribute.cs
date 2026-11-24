using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class ResourceAbsenceWorkflowAttribute : WorkflowAttribute
{
    public const string ReportStep = "absence-report";

    public const string RegisterStep = "absence-register";

    public const string SaveStep = "absence-save";

    public const string ApproveStep = "absence-approve";

    public const string HandleSickStep = "absence-handle-sick";

    public const string RejectStep = "absence-reject";

    public const string CancelStep = "absence-cancel";

    public const string CancelApprovedStep = "absence-cancel-approved";

    public const string AskStaffStep = "absence-ask-staff";

    public const string AskStaffAgainStep = "absence-ask-staff-again";

    public const string ContinueWithoutAnswerStep = "absence-continue-without-answer";

    public const string SaveQuestionStep = "absence-save-question";

    public const string AnswerStep = "absence-answer";

    public const string AskCoordinatorStep = "absence-ask-coordinator";

    public const string AskCoordinatorAgainStep = "absence-ask-coordinator-again";

    public const string AnswerStaffStep = "absence-answer-staff";

    public const string QuestionHandledStep = "absence-question-handled";

    public const string CancelQuestionStep = "absence-cancel-question";

    public const string CancelStaffQuestionStep = "absence-cancel-staff-question";

    public const string IsNewReason = "Only a new absence is reported or registered, and a stored one is saved";

    public const string StoredReason = "A new absence is reported or registered, and only a stored one is saved";

    public const string OwnReason = "A staff member reports their own absence, and a coordinator registers one on another staff member's behalf";

    public const string OnBehalfReason = "An absence is registered on another staff member's behalf, but your own is reported";

    public const string PlannedLeaveOnlyReason = "Only a planned leave is approved. A sick leave is marked handled";

    public const string SickLeaveOnlyReason = "Only a sick leave is marked handled. A planned leave is approved";

    public const string NotOwnDecisionReason = "An approver does not decide their own absence";

    public const string OwnQuestionReason = "Only the staff member asks, answers and changes their own absence while a question is with them";

    public const string NotOwnQuestionReason = "An approver does not ask or answer questions about their own absence";

    public const string OwnCancelReason = "Only the staff member cancels their own absence";

    public const string MayChangeReason = "Only the staff member (with Report Own Absence) or a holder of Manage Staff Absence changes or cancels it";

    public const string KindUnchangedReason = "The kind of a reported absence is not changed: cancel it and report the other kind";

    public const string PeriodValidReason = "The absence ends after it starts";

    public const string StaffInServiceReason = "The staff member is in service (their staff record and their account are active)";

    public const string KindKnownReason = "The absence is a planned leave or a sick leave";

    public const string NewByManagerReason = "An absence is registered on a staff member's behalf only by a holder of Manage Staff Absence";

    public static readonly TransitionCommentPrompt RejectPrompt = new("Reject the absence")
    {
        Message = "The staff member is told it was rejected and reads what you write on the absence.",
        Placeholder = "Why is it not approved?",
        RequiredMessage = "Write the reason first, or the staff member only gets a no.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AskStaffPrompt = new("Send a question to the staff member")
    {
        Message = "The absence goes back to the staff member with your question.",
        Placeholder = "What needs to be made clear?",
        RequiredMessage = "Write the question first, or the staff member only sees that something happened.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AskStaffAgainPrompt = new("Send another question")
    {
        Message = "The absence stays with the staff member with your new question.",
        Placeholder = "What else needs to be made clear?",
        RequiredMessage = "Write the question first, or it is only a reminder.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AnswerStaffPrompt = new("Answer the staff member")
    {
        Message = "The staff member sees your answer on the absence.",
        Placeholder = "Your answer",
        RequiredMessage = "Write the answer first, or the staff member only sees that something happened.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AskCoordinatorPrompt = new("Write a question to the coordinator")
    {
        Message = "The coordinator sees your question on the absence.",
        Placeholder = "What do you want to ask about?",
        RequiredMessage = "Write the question first, or the coordinator only sees that something happened.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AskCoordinatorAgainPrompt = new("Write another question")
    {
        Message = "The absence stays with the coordinator with your new question.",
        Placeholder = "What else do you want to ask about?",
        RequiredMessage = "Write the question first, or the coordinator sees nothing new.",
        Audience = CommentAudience.Staff,
    };

    public static readonly TransitionCommentPrompt AnswerPrompt = new("Answer the coordinator")
    {
        Message = "The absence goes back to the coordinator. You can send it without writing anything.",
        Placeholder = "Your answer",
        Required = false,
        Audience = CommentAudience.Staff,
    };

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<ResourceAbsenceState, ResourceAbsence>();

        foreach (var editable in new[] { ResourceAbsenceState.Requested, ResourceAbsenceState.Question })
        {
            workflow.AddState(editable)
                .ConfigureFields(f => f
                    .Default(FieldAccessFlags.ReadOnly)
                    .Field(e => e.Kind, FieldAccessFlags.ReadWrite)
                    .Field(e => e.FirstDay, FieldAccessFlags.Required)
                    .Field(e => e.StartTime, FieldAccessFlags.ReadWrite)
                    .Field(e => e.LastDay, FieldAccessFlags.Required)
                    .Field(e => e.EndTime, FieldAccessFlags.ReadWrite));
        }

        foreach (var locked in new[] { ResourceAbsenceState.Approved, ResourceAbsenceState.Rejected, ResourceAbsenceState.Canceled, ResourceAbsenceState.StaffQuestion })
        {
            workflow.AddState(locked).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        }

        workflow.AddTransition(new(SaveStep, ResourceAbsenceState.Requested, ResourceAbsenceState.Requested)
        {
            Label = "Save",
            Description = "Save the absence's changed period. It stays requested",
            Icon = "save",
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new DelegateCondition<ResourceAbsenceState, ResourceAbsence>(StoredReason, absence => !IsNewAbsence(absence)))
        .AddCondition(Required(e => e.FirstDay))
        .AddCondition(Required(e => e.LastDay))
        .AddCondition(new PeriodValid())
        .AddCondition(MayChange())
        .AddCondition(KindUnchanged());

        workflow.AddTransition(new(ReportStep, ResourceAbsenceState.Requested, ResourceAbsenceState.Requested)
        {
            Label = "Report",
            Description = "Report your absence: a sick leave blocks you from staffing at once, a planned leave once an approver approves it",
            Icon = "event_busy",
            RequiredPermission = RolePermission.ReportOwnAbsence,
            SortOrder = 30,
        })
        .AddCondition(IsNew())
        .AddCondition(DelegateCondition<ResourceAbsenceState, ResourceAbsence>.FromContext(OwnReason, context => IsOwn(context) == true, nameof(ResourceAbsence.Resource)))
        .AddCondition(KindKnown())
        .AddCondition(Required(e => e.FirstDay))
        .AddCondition(Required(e => e.LastDay))
        .AddCondition(new PeriodValid())
        .AddCondition(StaffInService())
        .AddAction(ApproversNotice(MessageTypes.AbsenceLeaveRequested, AbsenceKind.PlannedLeave))
        .AddAction(ApproversNotice(MessageTypes.AbsenceSickReported, AbsenceKind.SickLeave));

        workflow.AddTransition(new(RegisterStep, ResourceAbsenceState.Requested, ResourceAbsenceState.Requested)
        {
            Label = "Register",
            Description = "Register the absence on the staff member's behalf. It waits for an approver, and a sick leave blocks the staff member at once",
            Icon = "event_busy",
            RequiredPermission = RolePermission.ManageResourceAbsence,
            SortOrder = 30,
        })
        .AddCondition(IsNew())
        .AddCondition(NotOwn(OnBehalfReason))
        .AddCondition(KindKnown())
        .AddCondition(Required(e => e.FirstDay))
        .AddCondition(Required(e => e.LastDay))
        .AddCondition(new PeriodValid())
        .AddCondition(StaffInService())
        .AddAction(StaffNotice(MessageTypes.AbsenceRegistered, status: WaitingStatus))
        .AddAction(ApproversNotice(MessageTypes.AbsenceLeaveRequested, AbsenceKind.PlannedLeave))
        .AddAction(ApproversNotice(MessageTypes.AbsenceSickReported, AbsenceKind.SickLeave));

        foreach (var (key, kind) in new[] { (ApproveStep, AbsenceKind.PlannedLeave), (HandleSickStep, AbsenceKind.SickLeave) })
        {
            var sick = kind == AbsenceKind.SickLeave;
            workflow.AddTransition(new(key, ResourceAbsenceState.Requested, ResourceAbsenceState.Approved)
            {
                Label = sick ? "Handled" : "Approve",
                Description = sick
                    ? "Mark the sick report as handled. The staff member stays off staffing for that time"
                    : "Approve the absence: the staff member is not staffed for its days",
                Icon = sick ? "task_alt" : "event_available",
                RequiredPermission = RolePermission.ApproveResourceAbsence,
                SortOrder = 30,
                TakesNoEdits = true,
            })
            .AddCondition(new DelegateCondition<ResourceAbsenceState, ResourceAbsence>(sick ? SickLeaveOnlyReason : PlannedLeaveOnlyReason, e => e.Kind == kind))
            .AddCondition(NotOwn(NotOwnDecisionReason))
            .AddCondition(DelegateCondition<ResourceAbsenceState, ResourceAbsence>.FromContext(NewByManagerReason, context => !IsNewAbsence(context.Entity) || Guard(context).SessionManagesAbsence()))
            .AddCondition(KindKnown())
            .AddCondition(new PeriodValid())
            .AddCondition(StaffInService())
            .AddAction(StaffNotice(MessageTypes.AbsenceRegistered, status: DecidedStatus, when: context => IsNewAbsence(context.Entity)))
            .AddAction(StaffNotice(MessageTypes.AbsenceApproved, when: context => !IsNewAbsence(context.Entity)));
        }

        workflow.AddTransition(new(RejectStep, ResourceAbsenceState.Requested, ResourceAbsenceState.Rejected)
        {
            Label = "Reject",
            Description = "Reject the absence with a reason, which the staff member reads on it: it blocks nothing",
            Icon = "block",
            RequiredPermission = RolePermission.ApproveResourceAbsence,
            CommentPrompt = RejectPrompt,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
            TakesNoEdits = true,
        })
        .AddCondition(Stored())
        .AddCondition(NotOwn(NotOwnDecisionReason))
        .AddAction(StaffNotice(MessageTypes.AbsenceRejected));

        AddQuestionSteps(workflow);

        foreach (var (key, from) in new[] { (CancelStep, ResourceAbsenceState.Requested), (CancelApprovedStep, ResourceAbsenceState.Approved), (CancelQuestionStep, ResourceAbsenceState.Question), (CancelStaffQuestionStep, ResourceAbsenceState.StaffQuestion) })
        {
            var asked = from is ResourceAbsenceState.Question or ResourceAbsenceState.StaffQuestion;
            workflow.AddTransition(new(key, from, ResourceAbsenceState.Canceled)
            {
                Label = "Cancel absence",
                Description = asked
                    ? "Cancel the absence: the question lapses, it no longer blocks the staff member, but its history is kept"
                    : "Cancel the absence: it no longer blocks the staff member, but its history is kept",
                Icon = "event_available",
                ConfirmationMessage = "Cancel this absence? It no longer keeps the staff member from being staffed, but its history is kept.",
                SortOrder = 10,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            })
            .AddCondition(MayChange())
            .AddCondition(DelegateCondition<ResourceAbsenceState, ResourceAbsence>.FromContext(OwnCancelReason, context => IsOwn(context) == true, nameof(ResourceAbsence.Resource)))
            .AddAction(ApproversAbout(MessageTypes.AbsenceCanceledByStaff));
        }

        return workflow;
    }

    public static readonly IReadOnlyList<MessageType> StaffNoticeTypes = [MessageTypes.AbsenceQuestionFromStaff];

    private static MarkNoticesReadAction<ResourceAbsenceState, ResourceAbsence> SettleStaffNotices() => new(StaffNoticeTypes);

    private static void AddQuestionSteps(Workflow<ResourceAbsenceState, ResourceAbsence> workflow)
    {
        workflow.AddTransition(new(AskStaffStep, ResourceAbsenceState.Requested, ResourceAbsenceState.Question)
        {
            Label = "Send a question",
            Description = "You write a question in a window, and the absence goes back to the staff member, who answers",
            Icon = "help",
            RequiredPermission = RolePermission.ApproveResourceAbsence,
            CommentPrompt = AskStaffPrompt,
            SortOrder = 10,
            TakesNoEdits = true,
        })
        .AddCondition(Stored())
        .AddCondition(NotOwn(NotOwnQuestionReason))
        .AddAction(StaffNotice(MessageTypes.AbsenceQuestionToStaff, withComment: true));

        workflow.AddTransition(new(AskStaffAgainStep, ResourceAbsenceState.Question, ResourceAbsenceState.Question)
        {
            Label = "Send another question",
            Description = "The staff member gets one more question, and the absence stays with them",
            Icon = "help",
            RequiredPermission = RolePermission.ApproveResourceAbsence,
            CommentPrompt = AskStaffAgainPrompt,
            SortOrder = 20,
            TakesNoEdits = true,
        })
        .AddCondition(NotOwn(NotOwnQuestionReason))
        .AddAction(StaffNotice(MessageTypes.AbsenceQuestionToStaff, withComment: true));

        workflow.AddTransition(new(ContinueWithoutAnswerStep, ResourceAbsenceState.Question, ResourceAbsenceState.Requested)
        {
            Label = "Continue without an answer",
            Description = "You no longer wait for an answer: the absence goes back to be decided",
            Icon = "skip_next",
            RequiredPermission = RolePermission.ApproveResourceAbsence,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(NotOwn(NotOwnQuestionReason));

        workflow.AddTransition(new(AnswerStaffStep, ResourceAbsenceState.StaffQuestion, ResourceAbsenceState.Requested)
        {
            Label = "Answer the staff member",
            Description = "Your answer goes to the staff member, and the absence goes back to be decided",
            Icon = "forum",
            RequiredPermission = RolePermission.ApproveResourceAbsence,
            CommentPrompt = AnswerStaffPrompt,
            SortOrder = 20,
            TakesNoEdits = true,
        })
        .AddCondition(NotOwn(NotOwnQuestionReason))
        .AddAction(StaffNotice(MessageTypes.AbsenceAnswerToStaff, withComment: true))
        .AddAction(SettleStaffNotices());

        workflow.AddTransition(new(QuestionHandledStep, ResourceAbsenceState.StaffQuestion, ResourceAbsenceState.Requested)
        {
            Label = "Mark as handled",
            Description = "The question needs no written answer. The absence goes back to be decided, and nobody is told",
            Icon = "done_all",
            RequiredPermission = RolePermission.ApproveResourceAbsence,
            SortOrder = 30,
            TakesNoEdits = true,
        })
        .AddCondition(NotOwn(NotOwnQuestionReason))
        .AddAction(SettleStaffNotices());

        workflow.AddTransition(new(AskCoordinatorStep, ResourceAbsenceState.Requested, ResourceAbsenceState.StaffQuestion)
        {
            Label = "Write a question",
            Description = "A window opens where you write your question. The coordinator answers before the absence is decided",
            Icon = "contact_support",
            RequiredPermission = RolePermission.ReportOwnAbsence,
            CommentPrompt = AskCoordinatorPrompt,
            SortOrder = 10,
            TakesNoEdits = true,
        })
        .AddCondition(Stored())
        .AddCondition(Own(OwnQuestionReason))
        .AddAction(ApproversAbout(MessageTypes.AbsenceQuestionFromStaff, withComment: true));

        workflow.AddTransition(new(AskCoordinatorAgainStep, ResourceAbsenceState.StaffQuestion, ResourceAbsenceState.StaffQuestion)
        {
            Label = "Write another question",
            Description = "The coordinator gets one more question, and the absence stays with them",
            Icon = "contact_support",
            RequiredPermission = RolePermission.ReportOwnAbsence,
            CommentPrompt = AskCoordinatorAgainPrompt,
            SortOrder = 20,
            TakesNoEdits = true,
        })
        .AddCondition(Own(OwnQuestionReason))
        .AddAction(ApproversAbout(MessageTypes.AbsenceQuestionFromStaff, withComment: true));

        AddSaveRules(workflow.AddTransition(new(SaveQuestionStep, ResourceAbsenceState.Question, ResourceAbsenceState.Question)
        {
            Label = "Save",
            Description = "Save your changes. The absence stays with you until you answer",
            Icon = "save",
            RequiredPermission = RolePermission.ReportOwnAbsence,
            SortOrder = 20,
            IsSave = true,
        }));

        AddSaveRules(workflow.AddTransition(new(AnswerStep, ResourceAbsenceState.Question, ResourceAbsenceState.Requested)
        {
            Label = "Answer",
            Description = "You answer the coordinator: a window opens where you may write, and the absence goes back to be decided",
            Icon = "send",
            RequiredPermission = RolePermission.ReportOwnAbsence,
            CommentPrompt = AnswerPrompt,
            SortOrder = 30,
        }))
        .AddAction(ApproversAbout(MessageTypes.AbsenceAnswerFromStaff, withComment: true));
    }

    private static Transition<ResourceAbsenceState, ResourceAbsence> AddSaveRules(Transition<ResourceAbsenceState, ResourceAbsence> step) =>
        step.AddCondition(Stored())
            .AddCondition(Own(OwnQuestionReason))
            .AddCondition(Required(e => e.FirstDay))
            .AddCondition(Required(e => e.LastDay))
            .AddCondition(new PeriodValid())
            .AddCondition(KindUnchanged());

    public static bool IsNewAbsence(ResourceAbsence absence)
    {
        ArgumentNullException.ThrowIfNull(absence);
        return absence.Modified is null;
    }

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> IsNew() => new(IsNewReason, IsNewAbsence);

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> Stored() => new(StoredReason, absence => !IsNewAbsence(absence));

    private static RequiredFieldCondition<ResourceAbsenceState, ResourceAbsence> Required(System.Linq.Expressions.Expression<Func<ResourceAbsence, object?>> field) => new(field);

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> KindUnchanged() =>
        DelegateCondition<ResourceAbsenceState, ResourceAbsence>.FromContext(KindUnchangedReason, context => IsNewAbsence(context.Entity) || !Guard(context).KindChanged(context.Entity));

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> NotOwn(string description) =>
        DelegateCondition<ResourceAbsenceState, ResourceAbsence>.FromContext(description, context => IsOwn(context) == false, nameof(ResourceAbsence.Resource));

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> Own(string description) =>
        DelegateCondition<ResourceAbsenceState, ResourceAbsence>.FromContext(description, context => IsOwn(context) == true, nameof(ResourceAbsence.Resource));

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> MayChange() =>
        DelegateCondition<ResourceAbsenceState, ResourceAbsence>.FromContext(MayChangeReason, context => !IsNewAbsence(context.Entity) && context.Entity.Resource is not null && Guard(context).MayChange(context.Entity), nameof(ResourceAbsence.Resource));

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> StaffInService() =>
        new(StaffInServiceReason, e => e.Resource is { } resource && StaffServiceRules.IsInService(resource), nameof(ResourceAbsence.Resource), $"{nameof(ResourceAbsence.Resource)}.{nameof(Resource.User)}");

    private static DelegateCondition<ResourceAbsenceState, ResourceAbsence> KindKnown() => new(KindKnownReason, e => AbsenceRules.IsKnownKind(e.Kind));

    private static bool? IsOwn(WorkflowContext<ResourceAbsenceState, ResourceAbsence> context) =>
        context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { EffectiveUserId: { } actor } && context.Entity.Resource is { } resource
            ? resource.UserId == actor
            : null;

    private static IResourceAbsenceGuard Guard(WorkflowContext<ResourceAbsenceState, ResourceAbsence> context) =>
        context.ServiceProvider?.GetService(typeof(IResourceAbsenceGuard)) as IResourceAbsenceGuard
            ?? throw new InvalidOperationException("The absence workflow's steps are judged only where its absence guard is registered.");

    private static NotifyAction<ResourceAbsenceState, ResourceAbsence> ApproversNotice(MessageType type, AbsenceKind kind) =>
        new(type, absence => new Dictionary<string, string?>
        {
            ["Staff"] = StaffName(absence),
            ["Period"] = Period(absence),
            ["AbsenceId"] = absence.Id.ToString(),
        },
            recipientPermission: RolePermission.ApproveResourceAbsence,
            when: context => context.Entity.Kind == kind);

    private static NotifyAction<ResourceAbsenceState, ResourceAbsence> ApproversAbout(MessageType type, Func<WorkflowContext<ResourceAbsenceState, ResourceAbsence>, bool>? when = null, bool withComment = false) =>
        new(type, absence => new Dictionary<string, string?>
        {
            ["Staff"] = StaffName(absence),
            ["Kind"] = AbsenceRules.KindText(absence.Kind),
            ["Period"] = Period(absence),
            ["AbsenceId"] = absence.Id.ToString(),
        },
            recipientPermission: RolePermission.ApproveResourceAbsence,
            when: when,
            withComment: withComment);

    private static NotifyAction<ResourceAbsenceState, ResourceAbsence> StaffNotice(MessageType type, Func<ResourceAbsence, string>? status = null, Func<WorkflowContext<ResourceAbsenceState, ResourceAbsence>, bool>? when = null, bool withComment = false) =>
        new(type, absence =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["FirstName"] = absence.Resource?.User?.FirstName,
                    ["Kind"] = AbsenceRules.KindText(absence.Kind),
                    ["Period"] = Period(absence),
                    ["AbsenceId"] = absence.Id.ToString(),
                };
                if (status is not null)
                {
                    values["Status"] = status(absence);
                }

                return values;
            },
            recipients: absence => absence.Resource is { } resource ? [resource.UserId] : [],
            when: when,
            withComment: withComment);

    private static string WaitingStatus(ResourceAbsence absence) => absence.Kind == AbsenceKind.SickLeave
        ? "It keeps you from being staffed for that time already, and a coordinator marks it handled."
        : "It waits for an approver's decision.";

    private static string DecidedStatus(ResourceAbsence absence) => absence.Kind == AbsenceKind.SickLeave
        ? "It is handled: you are not staffed for that time."
        : "It is approved: you are not staffed for that time.";

    private static string Period(ResourceAbsence absence) => AbsenceRules.PeriodText(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime);

    private static string StaffName(ResourceAbsence absence) =>
        absence.Resource?.User is { } user && $"{user.FirstName} {user.LastName}".Trim() is { Length: > 0 } name ? name : "A staff member";

    private sealed class PeriodValid : Condition<ResourceAbsenceState, ResourceAbsence>
    {
        public PeriodValid() : base(PeriodValidReason)
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<ResourceAbsenceState, ResourceAbsence> context) =>
            AbsenceRules.Problems(context.Entity.FirstDay, context.Entity.StartTime, context.Entity.LastDay, context.Entity.EndTime);

        protected override bool IsMet(WorkflowContext<ResourceAbsenceState, ResourceAbsence> context) => GetBlockingReasons(context).Count == 0;
    }
}
