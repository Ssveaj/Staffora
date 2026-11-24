using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Integration;
using Staffora.Services.Portal;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class OrderSendTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly Start = new(2027, 2, 1);
    private static readonly DateOnly End = new(2027, 5, 31);

    private static readonly DateTimeOffset Anchor = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public OrderSendTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_coordinator_is_notified_of_the_job_which_carries_the_customers_default_unit_and_the_paired_contact()
    {
        var send = await SendScenarioAsync("Notified Staffing");

        ServiceHost? host = null;
        try
        {
            host = ServiceHost.Create(this.database, send.OrdererRuntime, FreshRuntimeForOtherScopes(send.OrdererRuntime));
            var sent = await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep);
            Assert.True(sent.IsSuccess, sent.Error);

            Assert.Contains(host.ContextLog, line => line.Contains(OrderSendFollowUp.Reason, StringComparison.Ordinal));
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }

        var request = await StoredRequestAsync(send.RequestId);
        var job = await TheJobAsync(send);
        var contact = await TheContactAsync(send);

        Assert.Equal(OrderState.Sent, request.State);
        Assert.Equal((JobState.AwaitingCoordination, send.Portal.Supplier.TenantId, send.UnitId, send.Portal.CustomerId, (Guid?)contact.Id), (job.State, job.TenantId, job.OrganizationUnitId, job.CustomerId, job.ContactId));
        Assert.NotEqual(send.Portal.RootId, job.OrganizationUnitId);
        Assert.Equal(("Algebra for year 9", "Two staff members who know the curriculum.", Start, End, (Guid?)null), (job.Name, job.Description, job.PeriodStart, job.PeriodEnd, job.CoordinatorUserId));
        Assert.Equal((request.LinkedCrossTenantId, request.CrossTenantId), (job.CrossTenantId, job.LinkedCrossTenantId));
        Assert.NotNull(request.CrossTenantId);
        Assert.NotNull(request.LinkedCrossTenantId);
        Assert.NotEqual(job.Id, request.LinkedCrossTenantId);
        Assert.NotEqual(request.Id, job.LinkedCrossTenantId);

        var orderer = await UserAsync(send.OrdererId);
        Assert.Equal((send.Portal.Supplier.TenantId, send.Portal.CustomerId, CustomerContactState.Active, (Guid?)send.OrdererId), (contact.TenantId, contact.CustomerId, contact.State, contact.LinkedUserId));
        Assert.Equal((orderer.FirstName, orderer.LastName, orderer.Email, (string?)null, (string?)null), (contact.FirstName, contact.LastName, contact.Email, contact.Phone, contact.Title));

        var notice = await TheNoticeAsync(send);
        Assert.Equal((MessageTypes.JobRequested.Key, OutgoingMessageStatus.Delivered, (RolePermission?)RolePermission.PerformCoordination, nameof(Job), job.Id), (notice.MessageType, notice.Status, notice.RecipientPermission, notice.EntityType, notice.EntityId));
        Assert.Equal($"?dlg=job-{job.Id}", notice.Link);
        Assert.Contains("Algebra for year 9", notice.Subject, StringComparison.Ordinal);
        Assert.Contains(JobRules.PeriodText(Start, End), notice.Text, StringComparison.Ordinal);
        var recipients = await RecipientsAsync(notice.Id);
        Assert.Contains(send.CoordinatorId, recipients);
        Assert.DoesNotContain(send.OrdererId, recipients);
        await using (var system = this.database.CreateSystemContext())
        {
            var tenants = await system.Users.AsNoTracking().Where(user => recipients.Contains(user.Id)).Select(user => user.TenantId).Distinct().ToListAsync(Ct);
            Assert.Equal([send.Portal.Supplier.TenantId], tenants);
        }

        await AssertOnTheSuppliersBehalfAsync(send, job.Id, contact.Id, notice.Id, impersonator: null);
    }

    [Fact]
    public async Task A_platform_owner_impersonating_the_orderer_sends_and_the_staffing_company_rows_name_neither_of_them()
    {
        var send = await SendScenarioAsync("Impersonated Staffing");
        var owner = send.Portal.Supplier.Platform;
        var runtime = owner.PlatformOwnerRuntime();
        runtime.StartImpersonation(send.OrdererId, send.Portal.PortalTenantId);

        await using (var host = ServiceHost.Create(this.database, runtime))
        {
            var sent = await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep);
            Assert.True(sent.IsSuccess, sent.Error);
        }

        var request = await StoredRequestAsync(send.RequestId);
        Assert.Equal((OrderState.Sent, (Guid?)send.OrdererId), (request.State, request.ModifiedById));
        await using (var system = this.database.CreateSystemContext())
        {
            var step = await system.StateHistoryEntries.AsNoTracking().SingleAsync(entry => entry.EntityId == send.RequestId && entry.TransitionKey == OrderWorkflowAttribute.SendStep, Ct);
            Assert.Equal((send.Portal.PortalTenantId, send.OrdererId, send.OrdererId, (Guid?)owner.OwnerId), (step.TenantId, step.ActorId, step.CreatedById, step.ImpersonatedById));
        }

        var job = await TheJobAsync(send);
        var contact = await TheContactAsync(send);
        var notice = await TheNoticeAsync(send);
        await AssertOnTheSuppliersBehalfAsync(send, job.Id, contact.Id, notice.Id, impersonator: owner.OwnerId);
    }

    public static TheoryData<string> Unavailable() => ["supplier suspended", "customer inactive", "supplier unlinked", "portal party inactive", "portal suspended"];

    [Theory]
    [MemberData(nameof(Unavailable))]
    public async Task A_held_suspended_unlinked_or_out_of_use_side_sends_nothing_and_the_portal_learns_nothing_of_why(string side)
    {
        var send = await SendScenarioAsync($"Unavailable Staffing");
        var expected = OrderSendFollowUp.SupplierUnavailable;

        string? judged = null;
        switch (side)
        {
            case "supplier suspended":
                await MoveTenantAsync(send, send.Portal.Supplier.TenantId, "tenant-suspend");
                judged = OrderSendFollowUp.TenantsNotInUse;
                break;
            case "customer inactive":
                await DeactivateCustomerAsync(send);
                judged = OrderSendFollowUp.CustomerNotInUse;
                break;
            case "supplier unlinked":
                var stranger = await OnboardedScenario.CreateAsync(this.database, Ct, "Stranger Staffing", activate: true);
                await ExecuteAsync($"UPDATE [Suppliers] SET [LinkedTenantId] = '{stranger.TenantId}' WHERE [TenantId] = '{send.Portal.PortalTenantId}'");
                judged = OrderSendFollowUp.TenantsNotInUse;
                break;
            case "portal party inactive":
                await ExecuteAsync($"UPDATE [Suppliers] SET [State] = {(int)SupplierState.Inactive} WHERE [TenantId] = '{send.Portal.PortalTenantId}'");
                expected = OrderWorkflowAttribute.SupplierActiveReason;
                break;
            case "portal suspended":
                await MoveTenantAsync(send, send.Portal.PortalTenantId, "tenant-suspend");
                expected = WorkflowService.HeldTenant;
                break;
        }

        var before = await SnapshotAsync(send);
        Result sent;
        var log = new SendLog();
        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime, services => services.AddSingleton<ILoggerProvider>(log)))
        {
            sent = await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep);
        }

        Assert.False(sent.IsSuccess);
        Assert.Contains(expected, sent.Error, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(send));
        if (judged is not null)
        {
            Assert.Contains(log.Lines, line => line.Contains($"was not sent: {judged}", StringComparison.Ordinal));
        }
    }

    private sealed class SendLog : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> lines = new();

        public IReadOnlyList<string> Lines => [.. this.lines];

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(OrderSendFollowUp).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => this.lines.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }

    public static TheoryData<string> FaultPoints() => ["the request's step save", "the contact", "the job", "the notice", "the commit"];

    [Theory]
    [MemberData(nameof(FaultPoints))]
    public async Task A_forced_failure_at_any_point_leaves_neither_side_half_written_and_a_later_send_goes_through(string point)
    {
        var send = await SendScenarioAsync("Faulty Staffing");
        var before = await SnapshotAsync(send);
        var fault = new Fault(point, send.Portal.Supplier.TenantId);

        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime, interceptors: [fault, fault.Commits]))
        {
            var sent = await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep);
            Assert.False(sent.IsSuccess);
        }

        Assert.True(fault.Struck);
        Assert.Equal(before, await SnapshotAsync(send));

        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime))
        {
            var sent = await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep);
            Assert.True(sent.IsSuccess, sent.Error);
        }

        var after = await SnapshotAsync(send);
        Assert.Equal((OrderState.Sent, 1, 1, 1), (after.RequestState, after.Jobs, after.Contacts, after.Notices));
    }

    [Fact]
    public async Task Sending_again_twice_at_once_or_a_request_whose_job_exists_writes_no_second_job_contact_or_notice()
    {
        var send = await SendScenarioAsync("Idempotent Staffing");

        var first = ServiceHost.Create(this.database, send.OrdererRuntime);
        var second = ServiceHost.Create(this.database, send.OrdererRuntime);
        try
        {
            var service1 = first.Get<OrderService>();
            var service2 = second.Get<OrderService>();
            var updater1 = (await service1.LoadOrderUpdaterAsync(send.RequestId, Ct)).Value!;
            var updater2 = (await service2.LoadOrderUpdaterAsync(send.RequestId, Ct)).Value!;
            var both = await Task.WhenAll(
                service1.UpdateOrderAndTransitionAsync(updater1, OrderWorkflowAttribute.SendStep, null, Ct),
                service2.UpdateOrderAndTransitionAsync(updater2, OrderWorkflowAttribute.SendStep, null, Ct));
            Assert.Single(both, result => result.IsSuccess);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        var once = await SnapshotAsync(send);
        Assert.Equal((OrderState.Sent, 1, 1, 1), (once.RequestState, once.Jobs, once.Contacts, once.Notices));

        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime))
        {
            Assert.False((await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        Assert.Equal(once, await SnapshotAsync(send));

        var links = await StoredRequestAsync(send.RequestId);
        await ExecuteAsync($"UPDATE [Orders] SET [State] = {(int)OrderState.Registration} WHERE [Id] = '{send.RequestId}'");
        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime))
        {
            var again = await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep);
            Assert.True(again.IsSuccess, again.Error);
        }

        var resent = await SnapshotAsync(send);
        Assert.Equal((OrderState.Sent, 1, 1, 1), (resent.RequestState, resent.Jobs, resent.Contacts, resent.Notices));
        var stored = await StoredRequestAsync(send.RequestId);
        Assert.Equal((links.CrossTenantId, links.LinkedCrossTenantId), (stored.CrossTenantId, stored.LinkedCrossTenantId));

        var next = await WriteAsync(send.OrdererRuntime, "Biology for year 8");
        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime))
        {
            Assert.True((await StepAsync(host, next, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        var two = await SnapshotAsync(send);
        Assert.Equal((2, 1, 2), (two.Jobs, two.Contacts, two.Notices));
        await using var system = this.database.CreateSystemContext();
        Assert.Single(await system.Jobs.AsNoTracking().Where(job => job.TenantId == send.Portal.Supplier.TenantId).Select(job => job.ContactId).Distinct().ToListAsync(Ct));
    }

    [Fact]
    public async Task Sending_needs_OrdersSend_and_another_orderer_is_paired_with_a_contact_of_their_own()
    {
        var send = await SendScenarioAsync("Gated Staffing");

        var writer = await TwoTenantScenario.AddUserAsync(this.database, send.Portal.Portal, "Writer", Ct);
        await TwoTenantScenario.GrantAsync(this.database, send.Portal.Portal, writer, Ct, RolePermission.OrdersView, RolePermission.OrdersCreate);
        var draft = await WriteAsync(send.Portal.RuntimeOf(writer), "Chemistry for year 10");
        var before = await SnapshotAsync(send);
        await using (var host = ServiceHost.Create(this.database, send.Portal.RuntimeOf(writer)))
        {
            Assert.False((await StepAsync(host, draft, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        Assert.Equal(before, await SnapshotAsync(send));
        Assert.Equal(OrderState.Registration, (await StoredRequestAsync(draft)).State);

        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime))
        {
            Assert.True((await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        var other = await send.Portal.AddOrdererAsync(this.database, "Pete", Ct);
        var theirs = await WriteAsync(send.Portal.RuntimeOf(other), "Drama for year 7");
        await using (var host = ServiceHost.Create(this.database, send.Portal.RuntimeOf(other)))
        {
            Assert.True((await StepAsync(host, theirs, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        await using var system = this.database.CreateSystemContext();
        var contacts = await system.CustomerContacts.AsNoTracking().Where(contact => contact.LinkedUserId == send.OrdererId || contact.LinkedUserId == other).ToListAsync(Ct);
        Assert.Equal(new[] { send.OrdererId, other }.Order(), contacts.Select(contact => contact.LinkedUserId!.Value).Order());
        Assert.All(contacts, contact => Assert.Equal((send.Portal.Supplier.TenantId, send.Portal.CustomerId), (contact.TenantId, contact.CustomerId)));
        Assert.Empty(await system.CustomerContacts.AsNoTracking().Where(contact => contact.TenantId == send.Portal.PortalTenantId).ToListAsync(Ct));
        var jobs = await system.Jobs.AsNoTracking().Where(job => job.TenantId == send.Portal.Supplier.TenantId).ToListAsync(Ct);
        Assert.Equal(contacts.Select(contact => (Guid?)contact.Id).Order(), jobs.Select(job => job.ContactId).Order());
    }

    [Fact]
    public async Task Nothing_of_the_staffing_company_comes_back_to_the_portal_but_the_requests_own_state_and_links()
    {
        var send = await SendScenarioAsync("Quiet Staffing");
        var draft = await StoredRequestAsync(send.RequestId);
        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime))
        {
            Assert.True((await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        var sent = await StoredRequestAsync(send.RequestId);
        Assert.Equal((draft.Name, draft.Description, draft.PeriodStart, draft.PeriodEnd, draft.SupplierId, draft.OrdererId), (sent.Name, sent.Description, sent.PeriodStart, sent.PeriodEnd, sent.SupplierId, sent.OrdererId));

        await using (var context = this.database.CreateContext(send.OrdererRuntime))
        {
            Assert.Empty(await context.Jobs.ToListAsync(Ct));
            Assert.Empty(await context.CustomerContacts.ToListAsync(Ct));
            Assert.Empty(await context.OutgoingMessages.ToListAsync(Ct));
            Assert.Empty(await context.StoredMessages.ToListAsync(Ct));
            Assert.Equal([send.Portal.PortalTenantId], await context.Tenants.Select(tenant => tenant.Id).ToListAsync(Ct));
            Assert.All(await context.StateHistoryEntries.Select(entry => entry.TenantId).ToListAsync(Ct), tenant => Assert.Equal(send.Portal.PortalTenantId, tenant));
        }

        await using (var context = this.database.CreateContext(send.Portal.Supplier.OwnerRuntime()))
        {
            Assert.Empty(await context.Orders.ToListAsync(Ct));
        }

        await using var reader = ServiceHost.Create(this.database, send.OrdererRuntime);
        var details = await reader.Get<OrderService>().ReadRequestAsync(send.RequestId, Ct);
        Assert.True(details.IsSuccess, details.Error);
        Assert.Equal((OrderState.Sent, true), (details.Value!.State, details.Value.Sent is not null));
    }

    [Fact]
    public async Task A_notice_whose_delivery_after_the_commit_failed_is_delivered_by_the_integration_outbox_once_it_is_a_minute_old()
    {
        var send = await SendScenarioAsync("Late Staffing");

        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime, services => services.AddSingleton<TimeProvider>(new AnchoredClock(Anchor))))
        {
            Assert.True((await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        Assert.Equal(OutgoingMessageStatus.Pending, (await TheNoticeAsync(send)).Status);

        var clock = new ManualClock(Anchor.AddMinutes(5));
        await using (var dispatcher = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        }))
        {
            var pass = await dispatcher.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct);
            Assert.True(pass.NoticesDelivered >= 1);
        }

        var notice = await TheNoticeAsync(send);
        Assert.Equal(OutgoingMessageStatus.Delivered, notice.Status);
        Assert.Contains(send.CoordinatorId, await RecipientsAsync(notice.Id));
    }

    [Fact]
    public async Task A_step_of_the_same_request_refused_meanwhile_in_the_same_circuit_does_not_leave_it_sent_without_its_job()
    {
        var send = await SendScenarioAsync("Circuit Staffing");

        var meanwhile = new Meanwhile(send.RequestId);
        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime, interceptors: [meanwhile]))
        {
            var service = host.Get<OrderService>();
            var second = (await service.LoadOrderUpdaterAsync(send.RequestId, Ct)).Value!;
            second.Entity.Name = "  ";
            meanwhile.Run = () => service.UpdateOrderAndTransitionAsync(second, OrderWorkflowAttribute.SendStep, null, Ct);
            var first = await service.LoadOrderUpdaterAsync(send.RequestId, Ct);
            var sent = await service.UpdateOrderAndTransitionAsync(first.Value!, OrderWorkflowAttribute.SendStep, null, Ct);
            Assert.True(meanwhile.Refused, "The second step did not run while the first one's send waited.");
            Assert.True(sent.IsSuccess, sent.Error);
        }

        var after = await SnapshotAsync(send);
        Assert.Equal((OrderState.Sent, 1, 1, 1), (after.RequestState, after.Jobs, after.Contacts, after.Notices));
    }

    private sealed class Meanwhile : SaveChangesInterceptor
    {
        private readonly Guid requestId;

        public Meanwhile(Guid requestId)
        {
            this.requestId = requestId;
        }

        public Func<Task<Result>>? Run { get; set; }

        public bool Refused { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var saving = eventData.Context?.ChangeTracker.Entries<Order>().Any(entry => entry.Entity.Id == this.requestId && entry.Entity.State == OrderState.Sent && entry.State == EntityState.Modified) ?? false;
            if (saving && Run is { } run)
            {
                Run = null;
                Refused = !(await run()).IsSuccess;
            }

            return result;
        }
    }

    [Fact]
    public async Task A_request_whose_links_supplier_or_orderer_the_dialog_changed_sends_none_of_them_and_steers_no_job()
    {
        var send = await SendScenarioAsync("Steered Staffing");

        await using (var host = ServiceHost.Create(this.database, send.OrdererRuntime))
        {
            Assert.True((await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        var target = await StoredRequestAsync(send.RequestId);
        var other = await send.Portal.AddOrdererAsync(this.database, "Pat", Ct);
        var forgedParty = Guid.NewGuid();
        var forgeries = new (string Field, Action<Order> Forge)[]
        {
            ("the links of a sent request", request => (request.CrossTenantId, request.LinkedCrossTenantId) = (target.CrossTenantId, target.LinkedCrossTenantId)),
            ("a link of its own choosing", request => request.CrossTenantId = Guid.NewGuid()),
            ("another supplier party", request => request.SupplierId = forgedParty),
            ("another orderer", request => request.OrdererId = other),
            ("another tenant", request => request.TenantId = send.Portal.Supplier.TenantId),
        };

        foreach (var (field, forge) in forgeries)
        {
            var draft = await WriteAsync(send.OrdererRuntime, $"Forged {field}");
            var before = await SnapshotAsync(send);
            foreach (var step in new[] { OrderWorkflowAttribute.SaveStep, OrderWorkflowAttribute.SendStep })
            {
                await using var host = ServiceHost.Create(this.database, send.OrdererRuntime);
                var service = host.Get<OrderService>();
                var updater = (await service.LoadOrderUpdaterAsync(draft, Ct)).Value!;
                forge(updater.Entity);
                Assert.False((await service.UpdateOrderAndTransitionAsync(updater, step, null, Ct)).IsSuccess, $"{step} with {field} went through.");
            }

            var stored = await StoredRequestAsync(draft);
            Assert.Equal((OrderState.Registration, (Guid?)null, (Guid?)null, send.OrdererId, send.Portal.PortalTenantId), (stored.State, stored.CrossTenantId, stored.LinkedCrossTenantId, stored.OrdererId, stored.TenantId));
            Assert.Equal(before with { RequestHistory = 0 }, await SnapshotAsync(send) with { RequestHistory = 0 });
        }
    }

    [Fact]
    public async Task No_key_of_either_tenant_names_a_row_or_a_person_of_the_other_but_the_links_the_send_is_built_on()
    {
        var send = await SendScenarioAsync("Swept Staffing");
        var owner = send.Portal.Supplier.Platform;
        var runtime = owner.PlatformOwnerRuntime();
        runtime.StartImpersonation(send.OrdererId, send.Portal.PortalTenantId);
        await using (var host = ServiceHost.Create(this.database, runtime, FreshRuntimeForOtherScopes(runtime)))
        {
            Assert.True((await StepAsync(host, send.RequestId, OrderWorkflowAttribute.SendStep)).IsSuccess);
        }

        var supplier = send.Portal.Supplier.TenantId;
        var portal = send.Portal.PortalTenantId;
        var job = await TheJobAsync(send);
        var contact = await TheContactAsync(send);
        var notice = await TheNoticeAsync(send);
        Assert.Equal(OutgoingMessageStatus.Delivered, notice.Status);
        Guid party;
        List<(string Schema, string Table)> tables;
        await using (var system = this.database.CreateSystemContext())
        {
            party = await system.Suppliers.AsNoTracking().Where(row => row.TenantId == portal).Select(row => row.Id).SingleAsync(Ct);
            tables = [.. system.Model.GetEntityTypes()
                .Where(type => typeof(ITenantEntity).IsAssignableFrom(type.ClrType) && type.GetTableName() is not null && type.BaseType is null)
                .Select(type => (Schema: type.GetSchema() ?? "dbo", Table: type.GetTableName()!))
                .Distinct()];
        }

        var ofThePortal = new[] { portal, send.RequestId, send.OrdererId, party, SystemAccount.IdFor(portal) };
        var ofTheSupplier = new[] { supplier, job.Id, contact.Id, notice.Id, send.Portal.CustomerId, send.UnitId, send.CoordinatorId, send.Portal.Supplier.OwnerId, SystemAccount.IdFor(supplier) };
        var allowed = new HashSet<(string Table, string Column, Guid Value)> { ("CustomerContacts", nameof(CustomerContact.LinkedUserId), send.OrdererId), ("Suppliers", nameof(Supplier.LinkedTenantId), supplier), ("Customers", nameof(Customer.LinkedTenantId), portal) };
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(Ct);
        var swept = 0;
        foreach (var (schema, table) in tables)
        {
            var keys = new List<string>();
            await using (var columns = new SqlCommand("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND DATA_TYPE = 'uniqueidentifier' AND COLUMN_NAME <> 'TenantId'", connection))
            {
                columns.Parameters.AddWithValue("@schema", schema);
                columns.Parameters.AddWithValue("@table", table);
                await using var reader = await columns.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    keys.Add(reader.GetString(0));
                }
            }

            foreach (var key in keys)
            {
                foreach (var (tenant, strangers) in new[] { (supplier, ofThePortal), (portal, ofTheSupplier) })
                {
                    foreach (var stranger in strangers)
                    {
                        if (allowed.Contains((table, key, stranger)))
                        {
                            continue;
                        }

                        await using var naming = new SqlCommand($"SELECT COUNT(*) FROM [{schema}].[{table}] WHERE TenantId = @tenant AND [{key}] = @stranger", connection);
                        naming.Parameters.AddWithValue("@tenant", tenant);
                        naming.Parameters.AddWithValue("@stranger", stranger);
                        Assert.True((int)(await naming.ExecuteScalarAsync(Ct))! == 0, $"{table}.{key} of tenant {(tenant == supplier ? "the staffing company" : "the portal")} names {stranger} of the other tenant");
                        swept++;
                    }
                }
            }
        }

        Assert.True(swept > 100);
        Assert.Equal((Guid?)send.OrdererId, contact.LinkedUserId);
    }

    public static TheoryData<string> Races() => ["the staffing company suspended", "the customer deactivated"];

    [Theory]
    [MemberData(nameof(Races))]
    public async Task A_send_racing_a_change_of_the_staffing_companys_side_either_goes_through_whole_or_writes_nothing(string race)
    {
        var send = await SendScenarioAsync("Racing Staffing");
        var before = await SnapshotAsync(send);
        await using var sender = ServiceHost.Create(this.database, send.OrdererRuntime, FreshRuntimeForOtherScopes(send.OrdererRuntime));
        var service = sender.Get<OrderService>();
        var updater = (await service.LoadOrderUpdaterAsync(send.RequestId, Ct)).Value!;

        var sending = Task.Run(() => service.UpdateOrderAndTransitionAsync(updater, OrderWorkflowAttribute.SendStep, null, Ct), Ct);
        var changing = race == "the staffing company suspended"
            ? Task.Run(() => MoveTenantAsync(send, send.Portal.Supplier.TenantId, "tenant-suspend"), Ct)
            : Task.Run(() => DeactivateCustomerAsync(send), Ct);
        await changing;
        var sent = await sending;

        var after = await SnapshotAsync(send);
        if (sent.IsSuccess)
        {
            Assert.Equal((OrderState.Sent, 1, 1, 1), (after.RequestState, after.Jobs, after.Contacts, after.Notices));
        }
        else
        {
            Assert.Equal(OrderSendFollowUp.SupplierUnavailable, sent.Error);
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task No_user_runs_the_jobs_receive_step_by_any_public_path()
    {
        var send = await SendScenarioAsync("Received Staffing");
        await using var host = ServiceHost.Create(this.database, send.Portal.Supplier.OwnerRuntime());
        var job = new Job { Name = "Forged arrival", CustomerId = send.Portal.CustomerId, OrganizationUnitId = send.UnitId, PeriodStart = Start, PeriodEnd = End };

        var direct = await host.Get<WorkflowService>().CreateAndTransitionAsync<JobState, Job>(job, JobWorkflowAttribute.ReceiveStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.False(direct.IsSuccess);
        Assert.Contains("is taken by the system alone", direct.Error, StringComparison.Ordinal);

        var registered = await host.Get<JobService>().CreateJobAsync(job, JobWorkflowAttribute.ReceiveStep, Ct);
        Assert.Equal(JobService.CreateByRegister, registered.Error);

        await using var system = this.database.CreateSystemContext();
        Assert.Equal(0, await system.Jobs.CountAsync(row => row.TenantId == send.Portal.Supplier.TenantId, Ct));
    }

    private async Task<SendScenario> SendScenarioAsync(string name)
    {
        var portal = await PortalScenario.CreateAsync(this.database, name, Ct, portalUnit: "Portal desk");
        await using var system = this.database.CreateSystemContext();
        var unitId = await system.Customers.AsNoTracking().Where(customer => customer.Id == portal.CustomerId).Select(customer => customer.DefaultUnitId!.Value).SingleAsync(Ct);
        var orderer = await portal.AddOrdererAsync(this.database, "Olive", Ct);
        var coordinator = await TwoTenantScenario.AddUserAsync(this.database, portal.Supplier.Tenant, "Cora", Ct);
        system.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole
        {
            TenantId = portal.Supplier.TenantId,
            UserId = coordinator,
            OrganizationUnitId = portal.RootId,
            TenantRoleId = portal.Supplier.Roles.Coordinator,
            CreatedById = portal.Supplier.OwnerId,
        });
        await system.SaveChangesAsync(Ct);
        var send = new SendScenario(portal, unitId, orderer, coordinator, Guid.Empty);
        return send with { RequestId = await WriteAsync(send.OrdererRuntime, "Algebra for year 9", "Two staff members who know the curriculum.") };
    }

    private sealed record SendScenario(PortalScenario Portal, Guid UnitId, Guid OrdererId, Guid CoordinatorId, Guid RequestId)
    {
        public IRuntimeExecutionContext OrdererRuntime => Portal.RuntimeOf(OrdererId);
    }

    private sealed record Snapshot(OrderState RequestState, Guid? CrossTenantId, Guid? LinkedCrossTenantId, int RequestHistory, int Jobs, int Contacts, int Notices, int SupplierHistory);

    private async Task<Snapshot> SnapshotAsync(SendScenario send)
    {
        await using var system = this.database.CreateSystemContext();
        var supplier = send.Portal.Supplier.TenantId;
        var request = await system.Orders.AsNoTracking().SingleAsync(row => row.Id == send.RequestId, Ct);
        return new Snapshot(
            request.State,
            request.CrossTenantId,
            request.LinkedCrossTenantId,
            await system.StateHistoryEntries.CountAsync(entry => entry.EntityId == send.RequestId, Ct),
            await system.Jobs.CountAsync(job => job.TenantId == supplier, Ct),
            await system.CustomerContacts.CountAsync(contact => contact.TenantId == supplier, Ct),
            await system.OutgoingMessages.CountAsync(notice => notice.TenantId == supplier && notice.MessageType == MessageTypes.JobRequested.Key, Ct),
            await system.StateHistoryEntries.CountAsync(entry => entry.TenantId == supplier && (entry.EntityType == nameof(Job) || entry.EntityType == nameof(CustomerContact)), Ct));
    }

    private async Task AssertOnTheSuppliersBehalfAsync(SendScenario send, Guid jobId, Guid contactId, Guid noticeId, Guid? impersonator)
    {
        var supplier = send.Portal.Supplier.TenantId;
        var account = SystemAccount.IdFor(supplier);
        await using var system = this.database.CreateSystemContext();
        var job = await system.Jobs.AsNoTracking().SingleAsync(row => row.Id == jobId, Ct);
        var contact = await system.CustomerContacts.AsNoTracking().SingleAsync(row => row.Id == contactId, Ct);
        var notice = await system.OutgoingMessages.AsNoTracking().SingleAsync(row => row.Id == noticeId, Ct);
        foreach (var (createdBy, impersonatedBy) in new[] { (job.CreatedById, job.ImpersonatedById), (contact.CreatedById, contact.ImpersonatedById), (notice.CreatedById, notice.ImpersonatedById) })
        {
            Assert.Equal((account, (Guid?)null), (createdBy, impersonatedBy));
        }

        var history = await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == jobId || entry.EntityId == contactId).ToListAsync(Ct);
        Assert.Equal(
            [(nameof(CustomerContact), CustomerContactWorkflowAttribute.ActivateStep, (int)CustomerContactState.Create, (int)CustomerContactState.Active), (nameof(Job), JobWorkflowAttribute.ReceiveStep, (int)JobState.Create, (int)JobState.AwaitingCoordination)],
            history.Select(entry => (entry.EntityType, entry.TransitionKey, entry.FromState, entry.ToState)).OrderBy(entry => entry.EntityType, StringComparer.Ordinal));
        Assert.All(history, entry => Assert.Equal((supplier, account, account, (Guid?)null), (entry.TenantId, entry.ActorId, entry.CreatedById, entry.ImpersonatedById)));
        Assert.Equal(history.Single(entry => entry.EntityId == jobId).Id, notice.HistoryEntryId);

        var strangers = new[] { send.OrdererId, impersonator ?? Guid.Empty }.Where(id => id != Guid.Empty).ToList();
        Assert.DoesNotContain(job.ModifiedById ?? Guid.Empty, strangers);
        Assert.DoesNotContain(contact.ModifiedById ?? Guid.Empty, strangers);
        Assert.DoesNotContain(job.CoordinatorUserId ?? Guid.Empty, strangers);
    }

    private async Task<Guid> WriteAsync(IRuntimeExecutionContext runtime, string name, string? description = null)
    {
        await using var host = ServiceHost.Create(this.database, runtime);
        var service = host.Get<OrderService>();
        var fresh = await service.NewOrderAsync(Ct);
        Assert.True(fresh.IsSuccess, fresh.Error);
        var request = fresh.Value!;
        (request.Name, request.Description, request.PeriodStart, request.PeriodEnd) = (name, description, Start, End);
        request.Schedule = JobSchedule.Recurring;
        var created = await service.CreateOrderAsync(request, OrderWorkflowAttribute.SaveDraftStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Result> StepAsync(ServiceHost host, Guid id, string step)
    {
        var service = host.Get<OrderService>();
        var updater = await service.LoadOrderUpdaterAsync(id, Ct);
        Assert.True(updater.IsSuccess, updater.Error);
        return await service.UpdateOrderAndTransitionAsync(updater.Value!, step, null, Ct);
    }

    private async Task MoveTenantAsync(SendScenario send, Guid tenantId, string step)
    {
        await using var host = ServiceHost.Create(this.database, send.Portal.Supplier.Platform.PlatformOwnerRuntime());
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, step, TenantWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private async Task DeactivateCustomerAsync(SendScenario send)
    {
        await using var host = ServiceHost.Create(this.database, send.Portal.Supplier.OwnerRuntime());
        var customers = host.Get<CustomerService>();
        var updater = await customers.LoadCustomerUpdaterAsync(send.Portal.CustomerId, Ct);
        Assert.True(updater.IsSuccess, updater.Error);
        var deactivated = await customers.UpdateCustomerAndTransitionAsync(updater.Value!, CustomerWorkflowAttribute.DeactivateStep, null, Ct);
        Assert.True(deactivated.IsSuccess, deactivated.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(CustomerState.Inactive, await system.Customers.AsNoTracking().Where(customer => customer.Id == send.Portal.CustomerId).Select(customer => customer.State).SingleAsync(Ct));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var system = this.database.CreateSystemContext();
#pragma warning disable EF1002
        await system.Database.ExecuteSqlRawAsync(sql, Ct);
#pragma warning restore EF1002
    }

    private async Task<Order> StoredRequestAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Orders.AsNoTracking().SingleAsync(request => request.Id == id, Ct);
    }

    private async Task<Job> TheJobAsync(SendScenario send)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Jobs.AsNoTracking().SingleAsync(job => job.TenantId == send.Portal.Supplier.TenantId, Ct);
    }

    private async Task<CustomerContact> TheContactAsync(SendScenario send)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.CustomerContacts.AsNoTracking().SingleAsync(contact => contact.TenantId == send.Portal.Supplier.TenantId, Ct);
    }

    private async Task<OutgoingMessage> TheNoticeAsync(SendScenario send)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.OutgoingMessages.AsNoTracking().SingleAsync(notice => notice.TenantId == send.Portal.Supplier.TenantId && notice.MessageType == MessageTypes.JobRequested.Key, Ct);
    }

    private async Task<ApplicationUser> UserAsync(Guid id)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.AsNoTracking().SingleAsync(user => user.Id == id, Ct);
    }

    private async Task<List<Guid>> RecipientsAsync(Guid noticeId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.StoredMessages.AsNoTracking().Where(stored => stored.OutgoingMessageId == noticeId).SelectMany(stored => stored.Recipients.Select(recipient => recipient.UserId)).ToListAsync(Ct);
    }

    private static Action<IServiceCollection> FreshRuntimeForOtherScopes(IRuntimeExecutionContext first)
    {
        var handed = 0;
        return services => services.AddScoped<IRuntimeExecutionContext>(_ => Interlocked.Exchange(ref handed, 1) == 0 ? first : new RuntimeExecutionContext());
    }

    private sealed class Fault : SaveChangesInterceptor
    {
        private bool struck;
        private readonly Guid supplierTenantId;

        public Fault(string point, Guid supplierTenantId)
        {
            this.supplierTenantId = supplierTenantId;
            Point = point;
        }

        public string Point { get; }

        public bool Struck => this.struck || Commits.Struck;

        public CommitFault Commits { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context?.ChangeTracker.Entries().ToList() ?? [];
            var strikes = Point switch
            {
                "the request's step save" => entries.Any(entry => entry.Entity is Order { State: OrderState.Sent } && entry.State == EntityState.Modified),
                "the contact" => entries.Any(entry => entry.Entity is CustomerContact contact && contact.TenantId == this.supplierTenantId && entry.State == EntityState.Added),
                "the job" => entries.Any(entry => entry.Entity is Job job && job.TenantId == this.supplierTenantId && entry.State == EntityState.Added),
                "the notice" => entries.Any(entry => entry.Entity is OutgoingMessage notice && notice.TenantId == this.supplierTenantId && entry.State == EntityState.Added),
                "the commit" => entries.Any(entry => entry.Entity is OutgoingMessage notice && notice.TenantId == this.supplierTenantId && entry.State == EntityState.Added) && Commits.Arm(),
                _ => throw new ArgumentOutOfRangeException(nameof(Point), Point, "Not a point of the send."),
            };
            if (strikes)
            {
                this.struck = true;
                throw new InvalidOperationException($"The forced failure at {Point}.");
            }

            return ValueTask.FromResult(result);
        }

        public sealed class CommitFault : DbTransactionInterceptor
        {
            private bool armed;

            public bool Struck { get; private set; }

            public bool Arm()
            {
                this.armed = true;
                return false;
            }

            public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            {
                if (this.armed)
                {
                    Struck = true;
                    throw new InvalidOperationException("The forced failure at the commit.");
                }

                return ValueTask.FromResult(result);
            }
        }
    }
}
