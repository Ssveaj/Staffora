using Staffora.Core.Jobs;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class OrderWorkflowAttribute : WorkflowAttribute
{
    public const string SaveDraftStep = "order-save-draft";

    public const string AreaLines = "Lines";

    public const string LinesValidReason = "Each line names one of the supplier's services and 1 to 20 people, at most ten lines";

    public const string LinesOfferedReason = "Every service of the order is one the supplier still offers";

    public const string SaveStep = "order-save";

    public const string SendStep = "order-send";

    public const string OwnTenantReason = "Only a user of this organization changes its orders";

    public const string SupplierActiveReason = "The staffing company takes orders from your organization";

    public const string OwnRequestReason = "An order is written in its orderer's own name";

    public const string NothingSetAheadReason = "A new order names no link to a job: sending it sets one";

    public static readonly IReadOnlyList<(OrderState State, string Slug)> Cancelable =
    [
        (OrderState.Registration, "draft"),
        (OrderState.Sent, "sent"),
        (OrderState.Coordinating, "coordinating"),
    ];

    private static readonly IReadOnlyList<(OrderState State, string Label, string Description, string Icon)> followedStates =
    [
        (OrderState.Coordinating, "Being staffed", "The staffing company started staffing the job", "edit_calendar"),
        (OrderState.Confirmed, "Confirmed", "The staffing company confirmed the job", "event_available"),
        (OrderState.InProgress, "Period started", "The job's period started", "play_circle"),
        (OrderState.Completed, "Completed", "The job's period ended and the job is completed", "task_alt"),
        (OrderState.CanceledBySupplier, "Canceled by the staffing company", "The staffing company canceled the job after confirming it", "event_busy"),
        (OrderState.DeclinedBySupplier, "Declined", "The staffing company declined the order before confirming the job", "block"),
        (OrderState.CanceledByUs, "Canceled by us", "The staffing company canceled the job as your organization asked", "cancel"),
    ];

    private const int Back = 10;
    private const int Neutral = 20;
    private const int Forward = 30;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<OrderState, Order>();

        workflow.AddState(OrderState.Create).ConfigureFields(f => Editable(f).Area(AreaLines, FieldAccessFlags.None, a => a.Field(e => e.Lines)));
        workflow.AddState(OrderState.Registration).ConfigureFields(f => Editable(f).Area(AreaLines, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Lines)));

        foreach (var kept in Enum.GetValues<OrderState>().Where(state => state > OrderState.Registration))
        {
            workflow.AddState(kept).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly).Area(AreaLines, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Lines)));
        }

        workflow[OrderState.Confirmed].AddEnterAction(OrdererNotice(MessageTypes.OrderConfirmed));
        workflow[OrderState.Completed].AddEnterAction(OrdererNotice(MessageTypes.OrderCompleted));
        workflow[OrderState.CanceledBySupplier].AddEnterAction(OrdererNotice(MessageTypes.OrderCanceledBySupplier));
        workflow[OrderState.DeclinedBySupplier].AddEnterAction(OrdererNotice(MessageTypes.OrderDeclined));

        AddSaveRules(workflow.AddTransition(new(SaveDraftStep, OrderState.Create, OrderState.Registration)
        {
            Label = "Save",
            Description = "Save the order as a draft: it is sent to the staffing company once it is complete",
            Icon = "save",
            RequiredPermission = RolePermission.OrdersCreate,
            ShowWhenBlocked = true,
            SortOrder = Neutral,
        }))
        .AddCondition(new DelegateCondition<OrderState, Order>(NothingSetAheadReason, e => e.CrossTenantId is null && e.LinkedCrossTenantId is null))
        .AddCondition(OrderedByTheSession());

        AddSaveRules(workflow.AddTransition(new(SaveStep, OrderState.Registration, OrderState.Registration)
        {
            Label = "Save",
            Description = "Save the draft's job, description, period and people. It is not sent yet",
            Icon = "save",
            RequiredPermission = RolePermission.OrdersCreate,
            SortOrder = Neutral,
            IsSave = true,
        }))
        .AddCondition(LinesValid());

        AddSaveRules(workflow.AddTransition(new(SendStep, OrderState.Registration, OrderState.Sent)
        {
            Label = "Send",
            Description = "Send the order to the staffing company: it receives it as a job, and its coordinators are told",
            Icon = "send",
            ConfirmationMessage = "Send this order to the staffing company? It can no longer be changed once it is sent.",
            RequiredPermission = RolePermission.OrdersSend,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        }))
        .AddCondition(LinesValid())
        .AddCondition(new LinesOffered())
        .AddAction(new DelegateAction<OrderState, Order>(context =>
        {
            var sender = Sender(context);
            sender.Link(context.Entity);
            var requestId = context.Entity.Id;
            context.AfterCommit.Add(token => sender.DeliverAsync(requestId, token));
        }));

        foreach (var (from, _) in Cancelable)
        {
            var cancel = workflow.AddTransition(new(CancelStepFrom(from), from, OrderSync.CanceledTo(from))
            {
                Label = "Cancel order",
                Description = from == OrderState.Registration
                    ? "Cancel the draft: it is never sent, and it is kept, canceled"
                    : "Cancel the order: the staffing company cancels the job, and its staff members are told. The order reads canceled once it has",
                Icon = "cancel",
                ConfirmationMessage = from == OrderState.Registration
                    ? "Cancel this draft? It is never sent, and it is kept, canceled."
                    : "Cancel this order? The staffing company cancels the job, and every staff member asked for it is told. The order reads \"Cancellation sent\" until the staffing company has answered, and is kept.",
                RequiredPermission = RolePermission.OrdersCancel,
                SortOrder = Back,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = true,
            });
            if (OrderSync.CancelCrosses(from))
            {
                cancel.AddAction(new DelegateAction<OrderState, Order>(context =>
                {
                    var sender = Sender(context);
                    var request = context.Entity;
                    context.BeforeSave.Add((store, token) => sender.QueueCancelAsync(store, request, token));
                }));
            }
        }

        foreach (var from in OrderSync.Following.Append(OrderState.CancellationSent))
        {
            foreach (var (to, label, description, icon) in followedStates.Where(followed => OrderSync.MovesForward(from, followed.State)))
            {
                if (to == OrderState.DeclinedBySupplier && from is not (OrderState.Sent or OrderState.Coordinating or OrderState.CancellationSent))
                {
                    continue;
                }

                workflow.AddTransition(new(FollowStepFrom(from, to), from, to)
                {
                    Label = label,
                    Description = description,
                    Icon = icon,
                    IsSystemDriven = true,
                    SortOrder = Forward,
                    TakesNoEdits = true,
                });
            }
        }

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    public static string CancelStepFrom(OrderState state) =>
        $"order-cancel-{Cancelable.FirstOrDefault(cancelable => cancelable.State == state).Slug ?? throw new ArgumentOutOfRangeException(nameof(state), state, "The orderer does not cancel an order in this state.")}";

    public static string FollowStepFrom(OrderState from, OrderState to) => $"order-follow-{Slug(to)}-from-{Slug(from)}";

    private static string Slug(OrderState state) =>
        string.Concat(state.ToString().Select((letter, index) => char.IsUpper(letter) && index > 0 ? $"-{char.ToLowerInvariant(letter)}" : char.ToLowerInvariant(letter).ToString()));

    private static NotifyAction<OrderState, Order> OrdererNotice(MessageType type) =>
        new(type, request => new Dictionary<string, string?>
        {
            ["Job"] = request.Name,
            ["Period"] = JobRules.WhenText(JobTimes.Of(request)),
            ["OrderId"] = request.Id.ToString(),
        },
            recipients: request => [request.OrdererId],
            namedRecipientsHold: RolePermission.OrdersView);

    private static Transition<OrderState, Order> AddSaveRules(Transition<OrderState, Order> transition) => transition
        .AddCondition(new RequiredFieldCondition<OrderState, Order>(e => e.Name))
        .AddCondition(new RequiredFieldCondition<OrderState, Order>(e => e.SupplierId))
        .AddCondition(new PeriodValid())
        .AddCondition(new DelegateCondition<OrderState, Order>(SupplierActiveReason, e => e.Supplier is { State: SupplierState.Active } supplier && supplier.Id == e.SupplierId, nameof(Order.Supplier)));

    private static DelegateCondition<OrderState, Order> OrderedByTheSession() =>
        DelegateCondition<OrderState, Order>.FromContext(OwnRequestReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { EffectiveUserId: { } user } && user == context.Entity.OrdererId);

    private static DelegateCondition<OrderState, Order> InOwnTenant() =>
        DelegateCondition<OrderState, Order>.FromContext(OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static IOrderSender Sender(WorkflowContext<OrderState, Order> context) =>
        context.ServiceProvider?.GetService(typeof(IOrderSender)) as IOrderSender
            ?? throw new InvalidOperationException("The send step runs only where an order sender is registered.");

    private static StateFieldBuilder<Order> Editable(StateFieldBuilder<Order> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.Description, FieldAccessFlags.ReadWrite)
        .Field(e => e.PeriodStart, FieldAccessFlags.Required)
        .Field(e => e.PeriodEnd, FieldAccessFlags.Required)
        .Field(e => e.Schedule, FieldAccessFlags.Required)
        .Field(e => e.StartTime, FieldAccessFlags.ReadWrite)
        .Field(e => e.EndTime, FieldAccessFlags.ReadWrite)
        .Field(e => e.PlaceName, FieldAccessFlags.ReadWrite)
        .Field(e => e.StreetAddress, FieldAccessFlags.ReadWrite)
        .Field(e => e.PostalCode, FieldAccessFlags.ReadWrite)
        .Field(e => e.City, FieldAccessFlags.ReadWrite);

    private static DelegateCondition<OrderState, Order> LinesValid() =>
        new(LinesValidReason, e => e.Lines.Count <= OrderLine.MaxLines && e.Lines.All(line =>
            line.Count is >= OrderLine.MinCount and <= OrderLine.MaxCount
            && line.OfferedService is { } copy && copy.Id == line.OfferedServiceId && copy.SupplierId == e.SupplierId),
            $"{nameof(Order.Lines)}.{nameof(OrderLine.OfferedService)}");

    private sealed class LinesOffered : Condition<OrderState, Order>
    {
        public LinesOffered() : base(LinesOfferedReason, [$"{nameof(Order.Lines)}.{nameof(OrderLine.OfferedService)}"])
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<OrderState, Order> context)
        {
            var reasons = base.GetBlockingReasons(context);
            var withdrawn = context.Entity.Lines.Where(line => line.OfferedService is { Offered: false }).Select(line => line.OfferedService.Name).Order(StringComparer.CurrentCulture).ToList();
            return reasons.Count > 0 && reasons[0] == Description && withdrawn.Count > 0
                ? [$"{string.Join(", ", withdrawn)} {(withdrawn.Count == 1 ? "is" : "are")} no longer offered. Remove {(withdrawn.Count == 1 ? "it" : "them")} and send again."]
                : reasons;
        }

        protected override bool IsMet(WorkflowContext<OrderState, Order> context) =>
            context.Entity.Lines.All(line => line.OfferedService is { Offered: true } copy && copy.Id == line.OfferedServiceId);
    }

    private sealed class PeriodValid : Condition<OrderState, Order>
    {
        public PeriodValid() : base(JobRules.ScheduleValidReason, [])
        {
        }

        public override IReadOnlyList<string> GetBlockingReasons(WorkflowContext<OrderState, Order> context)
        {
            var reasons = base.GetBlockingReasons(context);
            return reasons.Count > 0 && reasons[0] == Description ? JobRules.ScheduleProblems(JobTimes.Of(context.Entity)) : reasons;
        }

        protected override bool IsMet(WorkflowContext<OrderState, Order> context) => JobRules.ScheduleProblems(JobTimes.Of(context.Entity)).Count == 0;
    }
}
