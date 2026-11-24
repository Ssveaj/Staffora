using Staffora.Core.Jobs;
using Staffora.Core.Entities.Portal;
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

public class OrderWorkflowTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Orderer = Guid.NewGuid();

    private static Workflow<OrderState, Order> Requests => WorkflowRegistry.Default.Get<OrderState, Order>();

    [Fact]
    public void The_lifecycle_is_new_draft_and_sent_with_its_stored_values_and_its_steps_gates()
    {
        Assert.Equal(
            [
                (OrderState.Create, 0), (OrderState.Registration, 1), (OrderState.Sent, 2), (OrderState.Coordinating, 3),
                (OrderState.Confirmed, 4), (OrderState.InProgress, 5), (OrderState.Completed, 6), (OrderState.CanceledByUs, 7),
                (OrderState.CanceledBySupplier, 8), (OrderState.DeclinedBySupplier, 9), (OrderState.CancellationSent, 10),
            ],
            Enum.GetValues<OrderState>().Select(state => (state, (int)state)));
        var users = Requests.Transitions.Where(transition => !transition.IsSystemDriven).ToList();
        Assert.Equal(
            [
                (OrderWorkflowAttribute.SaveDraftStep, OrderState.Create, OrderState.Registration, (RolePermission?)RolePermission.OrdersCreate),
                (OrderWorkflowAttribute.SaveStep, OrderState.Registration, OrderState.Registration, RolePermission.OrdersCreate),
                (OrderWorkflowAttribute.SendStep, OrderState.Registration, OrderState.Sent, RolePermission.OrdersSend),
                ("order-cancel-draft", OrderState.Registration, OrderState.CanceledByUs, RolePermission.OrdersCancel),
                ("order-cancel-sent", OrderState.Sent, OrderState.CancellationSent, RolePermission.OrdersCancel),
                ("order-cancel-coordinating", OrderState.Coordinating, OrderState.CancellationSent, RolePermission.OrdersCancel),
            ],
            users.Select(transition => (transition.Key, transition.From, transition.To, transition.RequiredPermission)));
        Assert.All(users, transition => Assert.False(transition.RequiresPlatformOwner));
        Assert.All(users.Take(3), transition => Assert.False(transition.TakesNoEdits));

        Assert.All(users.Skip(3), transition => Assert.True(transition.TakesNoEdits && transition.ConfirmationMessage is not null));
        Assert.True(Requests.GetTransition(OrderWorkflowAttribute.SaveStep).IsSave);
        Assert.NotNull(Requests.GetTransition(OrderWorkflowAttribute.SendStep).ConfirmationMessage);

        Assert.Equal(OrderWorkflowAttribute.SaveStep, Requests.GetEditAuthority(OrderState.Registration)!.Key);
    }

    [Fact]
    public void A_new_request_and_a_draft_edit_the_job_and_its_period_and_a_sent_one_is_kept_as_it_was()
    {
        foreach (var state in new[] { OrderState.Create, OrderState.Registration })
        {
            var fields = Requests.GetFieldConfig(new Order { State = state });
            Assert.Equal(FieldAccessFlags.Required, fields.GetFieldAccess(nameof(Order.Name)));
            Assert.Equal(FieldAccessFlags.ReadWrite, fields.GetFieldAccess(nameof(Order.Description)));
            Assert.Equal(FieldAccessFlags.Required, fields.GetFieldAccess(nameof(Order.PeriodStart)));
            Assert.Equal(FieldAccessFlags.Required, fields.GetFieldAccess(nameof(Order.PeriodEnd)));

            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Order.SupplierId)));
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Order.OrdererId)));
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Order.CrossTenantId)));
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Order.LinkedCrossTenantId)));
        }

        var sent = Requests.GetFieldConfig(new Order { State = OrderState.Sent });
        foreach (var field in new[] { nameof(Order.Name), nameof(Order.Description), nameof(Order.PeriodStart), nameof(Order.PeriodEnd) })
        {
            Assert.Equal(FieldAccessFlags.ReadOnly, sent.GetFieldAccess(field));
        }
    }

    [Fact]
    public void Every_step_needs_the_requests_own_tenant_a_valid_period_the_job_and_its_staffing_company_in_use()
    {
        var environment = InTenant(Tenant, Orderer);
        foreach (var step in new[] { OrderWorkflowAttribute.SaveStep, OrderWorkflowAttribute.SendStep })
        {
            Assert.Empty(Requests.GetBlockingReasons(Draft(), step, environment));
            Assert.Equal([OrderWorkflowAttribute.OwnTenantReason], Requests.GetBlockingReasons(Draft(), step, InTenant(Guid.NewGuid(), Orderer)));
            Assert.Contains(OrderWorkflowAttribute.SupplierActiveReason, Requests.GetBlockingReasons(Draft(SupplierState.Inactive), step, environment));
            Assert.NotEmpty(Requests.GetBlockingReasons(Draft(start: new DateOnly(2027, 3, 1), end: new DateOnly(2027, 2, 1)), step, environment));

            var nameless = Draft();
            nameless.Name = " ";
            Assert.NotEmpty(Requests.GetBlockingReasons(nameless, step, environment));
        }

        Assert.Equal(JobRules.PeriodProblems(new DateOnly(2027, 3, 1), new DateOnly(2027, 2, 1)), Requests.GetBlockingReasons(Draft(start: new DateOnly(2027, 3, 1), end: new DateOnly(2027, 2, 1)), OrderWorkflowAttribute.SendStep, environment));
    }

    [Fact]
    public void A_new_request_is_written_in_its_orderers_own_name_with_no_link_set_ahead()
    {
        var fresh = Draft(state: OrderState.Create);
        Assert.Empty(Requests.GetBlockingReasons(fresh, OrderWorkflowAttribute.SaveDraftStep, InTenant(Tenant, Orderer)));
        Assert.Equal([OrderWorkflowAttribute.OwnRequestReason], Requests.GetBlockingReasons(fresh, OrderWorkflowAttribute.SaveDraftStep, InTenant(Tenant, Guid.NewGuid())));

        var linked = Draft(state: OrderState.Create);
        linked.CrossTenantId = Guid.NewGuid();
        Assert.Equal([OrderWorkflowAttribute.NothingSetAheadReason], Requests.GetBlockingReasons(linked, OrderWorkflowAttribute.SaveDraftStep, InTenant(Tenant, Orderer)));
    }

    [Fact]
    public async Task Send_links_the_request_through_the_sender_keeps_ids_it_holds_and_leaves_the_delivery_for_after_the_commit()
    {
        var sender = new RecordingSender();
        var environment = InTenant(Tenant, Orderer, sender);
        var request = Draft();
        var outcome = await Requests.ExecuteTransitionAsync(request, OrderWorkflowAttribute.SendStep, environment, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OrderState.Sent, request.State);
        Assert.Equal([request.Id], sender.Linked);
        Assert.NotNull(request.CrossTenantId);
        Assert.NotNull(request.LinkedCrossTenantId);
        Assert.NotEqual(request.CrossTenantId, request.LinkedCrossTenantId);

        Assert.Empty(sender.Delivered);
        var work = Assert.Single(outcome.AfterCommit);
        await work(TestContext.Current.CancellationToken);
        Assert.Equal([request.Id], sender.Delivered);

        var again = Draft();
        var (requestLink, jobLink) = (Guid.NewGuid(), Guid.NewGuid());
        (again.CrossTenantId, again.LinkedCrossTenantId) = (requestLink, jobLink);
        await Requests.ExecuteTransitionAsync(again, OrderWorkflowAttribute.SendStep, environment, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((requestLink, jobLink), (again.CrossTenantId!.Value, again.LinkedCrossTenantId!.Value));

        await Assert.ThrowsAnyAsync<Exception>(() => Requests.ExecuteTransitionAsync(Draft(), OrderWorkflowAttribute.SendStep, InTenant(Tenant, Orderer), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_sent_request_follows_its_job_through_system_driven_steps_that_lead_only_forward()
    {
        var followed = Requests.Transitions.Where(transition => transition.IsSystemDriven).ToList();
        Assert.NotEmpty(followed);
        Assert.All(followed, transition =>
        {
            Assert.True(OrderSync.MovesForward(transition.From, transition.To), transition.Key);
            Assert.Equal(OrderWorkflowAttribute.FollowStepFrom(transition.From, transition.To), transition.Key);
            Assert.Null(transition.RequiredPermission);
            Assert.True(transition.TakesNoEdits);
        });

        var expected = new List<(OrderState, OrderState)>();
        var forward = new[] { OrderState.Coordinating, OrderState.Confirmed, OrderState.InProgress, OrderState.Completed };
        foreach (var (from, index) in OrderSync.Following.Select((state, index) => (state, index)))
        {
            expected.AddRange(forward.Skip(index).Select(to => (from, to)));
            expected.Add((from, OrderState.CanceledBySupplier));
            expected.Add((from, OrderState.CanceledByUs));
            if (from is OrderState.Sent or OrderState.Coordinating)
            {
                expected.Add((from, OrderState.DeclinedBySupplier));
            }
        }

        expected.AddRange(OrderSync.Final.Select(to => (OrderState.CancellationSent, to)));

        Assert.Equal(expected.Order(), followed.Select(transition => (transition.From, transition.To)).Order());

        Assert.DoesNotContain(Requests.Transitions, transition => OrderSync.Final.Contains(transition.From));
        Assert.DoesNotContain(followed, transition => transition.To is OrderState.Create or OrderState.Registration or OrderState.Sent);
    }

    [Fact]
    public void The_orderer_cancels_until_the_job_is_confirmed_and_never_after()
    {
        var cancels = Requests.Transitions.Where(transition => transition.RequiredPermission == RolePermission.OrdersCancel).ToList();
        Assert.Equal([OrderState.Registration, OrderState.Sent, OrderState.Coordinating], cancels.Select(transition => transition.From));
        Assert.Equal([OrderState.CanceledByUs, OrderState.CancellationSent, OrderState.CancellationSent], cancels.Select(transition => transition.To));
        foreach (var state in Enum.GetValues<OrderState>())
        {
            Assert.Equal(OrderSync.OrdererCancels(state), cancels.Any(transition => transition.From == state));
        }

        foreach (var state in new[] { OrderState.Confirmed, OrderState.InProgress, OrderState.Completed, OrderState.CancellationSent })
        {
            Assert.DoesNotContain(Requests.GetTransitionsFrom(state), transition => !transition.IsSystemDriven);
        }
    }

    [Fact]
    public async Task A_sent_requests_cancellation_queues_its_message_before_the_save_and_a_drafts_tells_nobody()
    {
        var sender = new RecordingSender();
        var environment = InTenant(Tenant, Orderer, sender);

        var sent = Draft(state: OrderState.Sent);
        var outcome = await Requests.ExecuteTransitionAsync(sent, OrderWorkflowAttribute.CancelStepFrom(OrderState.Sent), environment, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(OrderState.CancellationSent, sent.State);
        Assert.Empty(sender.Canceled);
        var write = Assert.Single(outcome.BeforeSave);
        await write(null!, TestContext.Current.CancellationToken);
        Assert.Equal([sent.Id], sender.Canceled);
        Assert.Empty(outcome.OutgoingMessages);

        var draft = Draft();
        var drafted = await Requests.ExecuteTransitionAsync(draft, OrderWorkflowAttribute.CancelStepFrom(OrderState.Registration), environment, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(OrderState.CanceledByUs, draft.State);
        Assert.Empty(drafted.BeforeSave);
    }

    [Fact]
    public async Task The_orderer_is_told_when_the_job_is_confirmed_completed_canceled_by_the_staffing_company_or_declined()
    {
        var told = new Dictionary<OrderState, MessageType>
        {
            [OrderState.Confirmed] = MessageTypes.OrderConfirmed,
            [OrderState.Completed] = MessageTypes.OrderCompleted,
            [OrderState.CanceledBySupplier] = MessageTypes.OrderCanceledBySupplier,
            [OrderState.DeclinedBySupplier] = MessageTypes.OrderDeclined,
        };
        foreach (var to in new[] { OrderState.Coordinating, OrderState.Confirmed, OrderState.InProgress, OrderState.Completed, OrderState.CanceledBySupplier, OrderState.DeclinedBySupplier, OrderState.CanceledByUs })
        {
            var request = Draft(state: OrderState.Sent);
            var outcome = await Requests.ExecuteTransitionAsync(request, OrderWorkflowAttribute.FollowStepFrom(OrderState.Sent, to), InTenant(Tenant, Guid.NewGuid()), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(to, request.State);
            if (told.TryGetValue(to, out var type))
            {
                var notice = Assert.Single(outcome.OutgoingMessages);
                Assert.Equal((type.Key, (RolePermission?)RolePermission.OrdersView), (notice.MessageType, notice.NamedRecipientsHold));
                Assert.Equal([Orderer], notice.RecipientUserIds);
                Assert.Equal($"?dlg=order-{request.Id}", notice.Link);
            }
            else
            {
                Assert.Empty(outcome.OutgoingMessages);
            }
        }
    }

    private static Order Draft(SupplierState supplier = SupplierState.Active, DateOnly? start = null, DateOnly? end = null, OrderState state = OrderState.Registration)
    {
        var party = new Supplier { Id = Guid.NewGuid(), TenantId = Tenant, Name = "Northwind Staffing", State = supplier, LinkedTenantId = Guid.NewGuid() };
        return new Order
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            State = state,
            Name = "Algebra for year 9",
            PeriodStart = start ?? new DateOnly(2027, 2, 1),
            PeriodEnd = end ?? new DateOnly(2027, 5, 31),
            SupplierId = party.Id,
            Supplier = party,
            OrdererId = Orderer,
        };
    }

    private static WorkflowEnvironment InTenant(Guid tenantId, Guid userId, IOrderSender? sender = null) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Session(new RuntimeExecutionContext(tenantId, userId), sender));

    private sealed class Session : IServiceProvider
    {
        private readonly IRuntimeExecutionContext runtime;
        private readonly IOrderSender? sender;

        public Session(IRuntimeExecutionContext runtime, IOrderSender? sender)
        {
            this.runtime = runtime;
            this.sender = sender;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRuntimeExecutionContext) ? this.runtime : serviceType == typeof(IOrderSender) ? this.sender : null;
    }

    private sealed class RecordingSender : IOrderSender
    {
        public List<Guid> Linked { get; } = [];

        public List<Guid> Delivered { get; } = [];

        public void Link(Order request)
        {
            request.CrossTenantId ??= Guid.NewGuid();
            request.LinkedCrossTenantId ??= Guid.NewGuid();
            Linked.Add(request.Id);
        }

        public List<Guid> Canceled { get; } = [];

        public Task DeliverAsync(Guid requestId, CancellationToken cancellationToken)
        {
            Delivered.Add(requestId);
            return Task.CompletedTask;
        }

        public Task QueueCancelAsync(Microsoft.EntityFrameworkCore.DbContext store, Order request, CancellationToken cancellationToken)
        {
            Canceled.Add(request.Id);
            return Task.CompletedTask;
        }
    }
}
