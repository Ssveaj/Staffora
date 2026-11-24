using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Services.Integration;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class NoticeRetryTests : IClassFixture<RealDatabase>
{
    private const string Activate = "test-parent-activate";
    private static readonly DateTimeOffset Start = new(2027, 3, 1, 7, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public NoticeRetryTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_notice_whose_delivery_failed_is_delivered_after_its_back_off()
    {
        var clock = new ManualClock(Start);
        var (tenant, parent) = await ActivatedAsync(clock, services =>
        {
            services.RemoveAll<IChannelSender>();
            services.AddScoped<IChannelSender, FailingSender>();
        });

        var failed = await NoticeAsync(parent);
        Assert.Equal((OutgoingMessageStatus.Failed, 1, (DateTime?)(Start.UtcDateTime + IntegrationRetryRules.FirstRetry)), (failed.Status, failed.Attempts, failed.NextAttempt));

        await using var host = DispatcherHost(clock);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();
        clock.Advance(IntegrationRetryRules.FirstRetry - TimeSpan.FromSeconds(1));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesDelivered: 1), await dispatcher.RunOnceAsync(Ct));
        await AssertDeliveredOnceAsync(tenant, parent, attempts: 2);
    }

    [Fact]
    public async Task A_notice_left_pending_is_delivered_once_it_is_a_minute_old()
    {
        var clock = new ManualClock(Start);
        var (tenant, parent) = await ActivatedAsync(clock, NotDispatched);
        await using var host = DispatcherHost(clock);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        clock.Advance(IntegrationRetryRules.NoticeGrace - TimeSpan.FromSeconds(1));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal((OutgoingMessageStatus.Pending, 0), ((await NoticeAsync(parent)).Status, (await NoticeAsync(parent)).Attempts));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesDelivered: 1), await dispatcher.RunOnceAsync(Ct));
        await AssertDeliveredOnceAsync(tenant, parent, attempts: 1);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        await AssertDeliveredOnceAsync(tenant, parent, attempts: 1);
    }

    [Fact]
    public async Task A_notice_of_a_suspended_tenant_is_held_and_delivered_once_the_tenant_is_Active_again()
    {
        var clock = new ManualClock(Start);
        var (tenant, parent) = await ActivatedAsync(clock, NotDispatched);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await MoveAsync(platform, tenant.TenantId, "tenant-suspend");
        await using var host = DispatcherHost(clock);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal((OutgoingMessageStatus.Pending, 0), ((await NoticeAsync(parent)).Status, (await NoticeAsync(parent)).Attempts));
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.StoredMessages.AnyAsync(row => row.EntityId == parent, Ct));
        }

        await MoveAsync(platform, tenant.TenantId, "tenant-reactivate");
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesDelivered: 1), await dispatcher.RunOnceAsync(Ct));
        await AssertDeliveredOnceAsync(tenant, parent, attempts: 1);
    }

    [Fact]
    public async Task A_tenant_suspended_after_the_listing_holds_the_notices_claim()
    {
        var clock = new ManualClock(Start);
        var (tenant, parent) = await ActivatedAsync(clock, NotDispatched);

        var hook = new IntegrationOutboxTests.SuspendWhen(this.database.ConnectionString, tenant.TenantId, (command, context) => context?.RuntimeContext is null && command.CommandText.Contains("FROM [OutgoingMessages]", StringComparison.Ordinal) && command.CommandText.Contains("TOP(1)", StringComparison.Ordinal));
        await using var host = DispatcherHost(clock, interceptors: [hook]);
        clock.Advance(IntegrationRetryRules.NoticeGrace);
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesHeld: 1), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.True(hook.Fired);

        var held = await NoticeAsync(parent);
        Assert.Equal((OutgoingMessageStatus.Pending, 0, (DateTime?)null, (DateTime?)null), (held.Status, held.Attempts, held.NextAttempt, held.Modified));

        await SetStateAsync(tenant.TenantId, TenantState.Active);
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesDelivered: 1), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        await AssertDeliveredOnceAsync(tenant, parent, attempts: 1);
    }

    [Fact]
    public async Task A_tenant_suspended_after_the_claim_holds_the_notices_delivery_and_the_attempt_is_given_back()
    {
        var clock = new ManualClock(Start);
        var (tenant, parent) = await ActivatedAsync(clock, services =>
        {
            services.RemoveAll<IChannelSender>();
            services.AddScoped<IChannelSender, FailingSender>();
        });

        var hook = new IntegrationOutboxTests.SuspendBeforeDelivery(this.database.ConnectionString, tenant.TenantId);
        await using var host = DispatcherHost(clock, interceptors: [hook]);
        clock.Advance(IntegrationRetryRules.FirstRetry);
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesHeld: 1), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.True(hook.Fired);
        var held = await NoticeAsync(parent);
        Assert.Equal((OutgoingMessageStatus.Failed, 1, (DateTime?)(Start.UtcDateTime + IntegrationRetryRules.FirstRetry)), (held.Status, held.Attempts, held.NextAttempt));
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.StoredMessages.AnyAsync(row => row.EntityId == parent, Ct));
        }

        await SetStateAsync(tenant.TenantId, TenantState.Active);
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesDelivered: 1), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        await AssertDeliveredOnceAsync(tenant, parent, attempts: 2);
    }

    [Theory]
    [InlineData(TenantState.Archived)]
    [InlineData(TenantState.PendingDeletion)]
    public async Task A_notice_of_a_tenant_scheduled_for_deletion_or_archived_is_left_alone(TenantState state)
    {
        var clock = new ManualClock(Start);
        var (tenant, parent) = await ActivatedAsync(clock, NotDispatched);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)state} WHERE Id = {tenant.TenantId}", Ct);
        }

        await using var host = DispatcherHost(clock);
        clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.Equal((OutgoingMessageStatus.Pending, 0), ((await NoticeAsync(parent)).Status, (await NoticeAsync(parent)).Attempts));
    }

    [Fact]
    public async Task A_notice_that_keeps_failing_is_given_up_after_its_last_attempt()
    {
        var clock = new ManualClock(Start);
        Action<IServiceCollection> failing = services =>
        {
            services.RemoveAll<IChannelSender>();
            services.AddScoped<IChannelSender, FailingSender>();
        };
        var (_, parent) = await ActivatedAsync(clock, failing);
        await using var host = DispatcherHost(clock, failing);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        for (var attempts = 1; attempts < IntegrationRetryRules.MaxAttempts; attempts++)
        {
            clock.Advance(IntegrationRetryRules.RetryAfter(attempts));
            Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0, NoticesFailed: 1), await dispatcher.RunOnceAsync(Ct));
        }

        var given = await NoticeAsync(parent);
        Assert.Equal((OutgoingMessageStatus.Failed, IntegrationRetryRules.MaxAttempts, (DateTime?)null), (given.Status, given.Attempts, given.NextAttempt));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
    }

    private async Task<(ScenarioTenant Tenant, Guid Parent)> ActivatedAsync(ManualClock clock, Action<IServiceCollection> configure)
    {
        await using (var settle = this.database.CreateSystemContext())
        {
            await settle.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutgoingMessages SET Status = {(int)OutgoingMessageStatus.Failed}, NextAttempt = NULL WHERE Status <> {(int)OutgoingMessageStatus.Delivered}", Ct);
        }

        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Company Notices"), Ct);
        Guid parent;
        await using (var writer = this.database.CreateContext(tenant.Runtime(), clock))
        {
            using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
            var row = new TestParent { Name = "Parent", State = TestParentState.Draft };
            writer.TestParents.Add(row);
            await writer.SaveChangesAsync(Ct);
            parent = row.Id;
        }

        await using var stepping = ServiceHost.Create(this.database, tenant.Runtime(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            configure(services);
        });
        var activated = await stepping.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: Ct);
        Assert.True(activated.IsSuccess, activated.Error);
        return (tenant, parent);
    }

    private static void NotDispatched(IServiceCollection services)
    {
        services.RemoveAll<IOutgoingMessageDispatcher>();
        services.AddScoped<IOutgoingMessageDispatcher, NotYetDispatched>();
    }

    private ServiceHost DispatcherHost(ManualClock clock, Action<IServiceCollection>? configure = null, IReadOnlyList<IInterceptor>? interceptors = null) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
            configure?.Invoke(services);
        }, interceptors);

    private async Task SetStateAsync(Guid tenantId, TenantState state)
    {
        await using var system = this.database.CreateSystemContext();
        await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)state} WHERE Id = {tenantId}", Ct);
    }

    private async Task MoveAsync(ScenarioTenant platform, Guid tenantId, string step)
    {
        await using var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId));
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, step, cancellationToken: Ct);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private async Task<OutgoingMessage> NoticeAsync(Guid parent)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: read a parent's notice");
        return await context.OutgoingMessages.AsNoTracking().SingleAsync(row => row.EntityId == parent, Ct);
    }

    private async Task AssertDeliveredOnceAsync(ScenarioTenant tenant, Guid parent, int attempts)
    {
        var notice = await NoticeAsync(parent);
        Assert.Equal((OutgoingMessageStatus.Delivered, attempts, 1, (Guid?)tenant.SystemAccountId), (notice.Status, notice.Attempts, notice.RecipientCount, notice.ModifiedById));
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: read a parent's delivered notice");
        var stored = await context.StoredMessages.AsNoTracking().Include(row => row.Recipients).SingleAsync(row => row.EntityId == parent, Ct);
        Assert.Equal((tenant.TenantId, tenant.SystemAccountId), (stored.TenantId, stored.CreatedById));
        Assert.Equal([tenant.OwnerId], stored.Recipients.Select(recipient => recipient.UserId));
    }

    private sealed class FailingSender : IChannelSender
    {
        public MessageChannel Channel => MessageChannel.InApp;

        public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken) => throw new InvalidOperationException("The in-app channel is down.");
    }

    private sealed class NotYetDispatched : IOutgoingMessageDispatcher
    {
        public Task DispatchAsync(IReadOnlyList<OutgoingMessage> messages, Guid historyEntryId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
