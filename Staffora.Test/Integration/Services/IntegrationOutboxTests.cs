using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Data;
using Staffora.Services.Diagnostics;
using Staffora.Services.Integration;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class IntegrationOutboxTests : IClassFixture<RealDatabase>
{
    private const string FolderKind = "test.folder";
    private static readonly DateTimeOffset Start = new(2027, 2, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public IntegrationOutboxTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_is_handled_in_its_target_tenant_as_its_system_account_and_marked_delivered_by_the_senders()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        await using var host = DispatcherHost(clock, probe);

        Assert.Equal(new IntegrationOutboxPass(1, 0, 0, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));

        var folder = await FolderAsync(payload);
        Assert.Equal((customer.TenantId, customer.SystemAccountId, (Guid?)null), (folder.TenantId, folder.CreatedById, folder.ImpersonatedById));
        var receipt = await ReceiptAsync(id);
        Assert.Equal((customer.TenantId, customer.SystemAccountId, FolderKind), (receipt.TenantId, receipt.CreatedById, receipt.Kind));

        var message = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Delivered, 1, company.OwnerId, (Guid?)company.SystemAccountId, (DateTime?)Start.UtcDateTime, (DateTime?)null), (message.State, message.Attempts, message.CreatedById, message.ModifiedById, message.Finished, message.ClaimedUntil));

        Assert.Equal(0, await FolderCountAsync(payload, company.TenantId));
        foreach (var reason in new[] { IntegrationOutboxDispatcher.ListReason, IntegrationOutboxDispatcher.ClaimReason, IntegrationOutboxDispatcher.DeliverReason })
        {
            Assert.Contains($"System scope opened for {reason} (depth 1).", host.ContextLog);
        }

        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.Equal([id], probe.Calls);
    }

    [Fact]
    public async Task A_failed_delivery_writes_nothing_and_is_tried_again_after_its_back_off()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        probe.FailTimes(id, 2);
        await using var host = DispatcherHost(clock, probe);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        Assert.Equal(new IntegrationOutboxPass(0, 1, 0, 0), await dispatcher.RunOnceAsync(Ct));
        var failed = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Pending, 1, Start.UtcDateTime + TimeSpan.FromSeconds(30), (DateTime?)null, nameof(InvalidOperationException)), (failed.State, failed.Attempts, failed.NextAttempt, failed.ClaimedUntil, failed.LastFailure));
        Assert.Equal(0, await FolderCountAsync(payload));
        Assert.False(await ReceiptExistsAsync(id));

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new IntegrationOutboxPass(0, 1, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal((2, clock.Now.UtcDateTime + TimeSpan.FromMinutes(1)), ((await MessageAsync(id)).Attempts, (await MessageAsync(id)).NextAttempt));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new IntegrationOutboxPass(1, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        var delivered = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Delivered, 3, (string?)null), (delivered.State, delivered.Attempts, delivered.LastFailure));
        Assert.Equal(1, await FolderCountAsync(payload));
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_a_dead_letter_after_its_last_attempt_and_the_next_of_its_correlation_then_goes()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var correlation = Guid.NewGuid();
        var (failing, next) = (Payload(), Payload());
        var failingId = await SendAsync(company, customer.TenantId, correlation, failing, clock);
        var nextId = await SendAsync(company, customer.TenantId, correlation, next, clock);
        probe.FailTimes(failingId, int.MaxValue);
        var logged = new ConcurrentQueue<(LogLevel Level, string Message)>();
        await using var host = DispatcherHost(clock, probe, services => services.AddLogging(logging => logging.AddProvider(new LogRecorder(logged))));
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        for (var attempt = 1; attempt < IntegrationRetryRules.MaxAttempts; attempt++)
        {
            Assert.Equal(new IntegrationOutboxPass(0, 1, 0, 0), await dispatcher.RunOnceAsync(Ct));
            clock.Advance(IntegrationRetryRules.RetryAfter(attempt));
        }

        Assert.Equal(new IntegrationOutboxPass(1, 0, 1, 0), await dispatcher.RunOnceAsync(Ct));
        var dead = await MessageAsync(failingId);
        Assert.Equal((IntegrationMessageState.DeadLettered, IntegrationRetryRules.MaxAttempts, (DateTime?)clock.Now.UtcDateTime, nameof(InvalidOperationException)), (dead.State, dead.Attempts, dead.Finished, dead.LastFailure));
        Assert.Contains(logged, entry => entry.Level == LogLevel.Error && entry.Message.Contains($"{failingId}") && entry.Message.Contains("dead letter"));
        Assert.Equal((0, 1), (await FolderCountAsync(failing), await FolderCountAsync(next)));
        Assert.Equal(IntegrationMessageState.Delivered, (await MessageAsync(nextId)).State);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal(IntegrationRetryRules.MaxAttempts, probe.Calls.Count(call => call == failingId));
    }

    [Fact]
    public async Task A_message_delivered_again_after_its_mark_was_lost_runs_its_handler_once()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        await using var host = DispatcherHost(clock, probe);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();
        Assert.Equal(1, (await dispatcher.RunOnceAsync(Ct)).Delivered);

        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE IntegrationMessages SET State = {(int)IntegrationMessageState.Pending}, ClaimToken = NULL, ClaimedUntil = NULL, Finished = NULL WHERE Id = {id}", Ct);
        }

        Assert.Equal(1, (await dispatcher.RunOnceAsync(Ct)).Delivered);
        Assert.Equal([id], probe.Calls);
        Assert.Equal(1, await FolderCountAsync(payload));
        await using var check = this.database.CreateSystemContext();
        Assert.Equal(1, await check.IntegrationReceipts.CountAsync(receipt => receipt.MessageId == id, Ct));
        Assert.Equal((IntegrationMessageState.Delivered, 2), ((await MessageAsync(id)).State, (await MessageAsync(id)).Attempts));
    }

    [Fact]
    public async Task A_handler_cannot_write_outside_its_target_tenant()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        probe.WriteInto(id, company.TenantId);
        await using var host = DispatcherHost(clock, probe);

        Assert.Equal(new IntegrationOutboxPass(0, 1, 0, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.Equal(nameof(TenantIsolationException), (await MessageAsync(id)).LastFailure);
        Assert.Equal(0, await FolderCountAsync(payload));
        Assert.False(await ReceiptExistsAsync(id));
    }

    [Fact]
    public async Task A_message_between_tenants_that_are_not_linked_is_a_dead_letter_and_writes_nothing()
    {
        var (company, _) = await LinkedAsync();
        var stranger = await TwoTenantScenario.AddTenantAsync(this.database, "Stranger", Ct);
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, stranger.TenantId, Guid.NewGuid(), payload, clock);
        await using var host = DispatcherHost(clock, probe);

        Assert.Equal(new IntegrationOutboxPass(0, 0, 1, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        var message = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.DeadLettered, 0, IntegrationOutboxDispatcher.NotLinkedReason, (Guid?)company.SystemAccountId), (message.State, message.Attempts, message.LastFailure, message.ModifiedById));
        Assert.Empty(probe.Calls);
        Assert.Equal(0, await FolderCountAsync(payload));
    }

    [Fact]
    public async Task A_pass_reports_each_dispatch_with_its_kind_and_outcome_and_reads_the_backlog_while_a_meter_listens()
    {
        var (company, _) = await LinkedAsync();
        var stranger = await TwoTenantScenario.AddTenantAsync(this.database, "Stranger", Ct);
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var id = await SendAsync(company, stranger.TenantId, Guid.NewGuid(), Payload(), clock);
        await using var host = DispatcherHost(clock, probe);
        var meter = host.Get<StafforaTelemetry>().Meter;
        var measured = new ConcurrentQueue<(string Instrument, long Value, string? Kind, string? Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, listening) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listening.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measured.Enqueue((instrument.Name, value, null, null)));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            string? kind = null, outcome = null;
            foreach (var (key, tag) in tags)
            {
                (kind, outcome) = key switch
                {
                    StafforaTelemetry.MessageKindTag => (tag as string, outcome),
                    StafforaTelemetry.OutcomeTag => (kind, tag as string),
                    _ => (kind, outcome),
                };
            }

            measured.Enqueue((instrument.Name, 0, kind, outcome));
        });
        listener.Start();
        var dispatched = new ConcurrentQueue<Activity>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StafforaTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = dispatched.Enqueue,
        };
        ActivitySource.AddActivityListener(activities);

        Assert.Equal(new IntegrationOutboxPass(0, 0, 1, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));

        await using var system = this.database.CreateSystemContext();
        var deadLetters = await system.IntegrationMessages.LongCountAsync(message => message.State == IntegrationMessageState.DeadLettered, Ct);
        Assert.Contains((StafforaTelemetry.OutboxDispatchDuration, 0L, FolderKind, "dead_lettered"), measured);
        Assert.Contains((StafforaTelemetry.OutboxPending, 0L, (string?)null, (string?)null), measured);
        Assert.Contains((StafforaTelemetry.OutboxDeadLetters, deadLetters, (string?)null, (string?)null), measured);
        Assert.Contains($"System scope opened for {IntegrationOutboxDispatcher.BacklogReason} (depth 1).", host.ContextLog);
        var dispatch = Assert.Single(dispatched, activity => Equals(activity.GetTagItem(StafforaTelemetry.MessageIdTag), id.ToString()));
        Assert.Equal((StafforaTelemetry.OutboxDispatchActivity, FolderKind, "dead_lettered"), (dispatch.OperationName, dispatch.GetTagItem(StafforaTelemetry.MessageKindTag), dispatch.GetTagItem(StafforaTelemetry.OutcomeTag)));
    }

    [Fact]
    public async Task A_pass_reads_no_backlog_while_no_meter_listens()
    {
        await LinkedAsync();
        await using var host = DispatcherHost(new ManualClock(Start), new HandlerProbe());

        await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct);

        Assert.DoesNotContain(host.ContextLog, line => line.Contains(IntegrationOutboxDispatcher.BacklogReason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_message_for_a_tenant_that_is_not_Active_is_held_and_delivered_once_it_is_Active_again(bool targetSuspended)
    {
        var (company, customer) = await LinkedAsync();
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        var suspended = targetSuspended ? customer : company;
        await MoveAsync(platform, suspended.TenantId, "tenant-suspend");
        await using var host = DispatcherHost(clock, probe);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal((IntegrationMessageState.Pending, 0), ((await MessageAsync(id)).State, (await MessageAsync(id)).Attempts));
        Assert.Empty(probe.Calls);

        await MoveAsync(platform, suspended.TenantId, "tenant-reactivate");
        Assert.Equal(new IntegrationOutboxPass(1, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal((IntegrationMessageState.Delivered, 1), ((await MessageAsync(id)).State, (await MessageAsync(id)).Attempts));
        Assert.Equal(1, await FolderCountAsync(payload, customer.TenantId));
    }

    [Theory]
    [InlineData(TenantState.Archived)]
    [InlineData(TenantState.PendingDeletion)]
    [InlineData(TenantState.Setup)]
    public async Task A_message_for_a_tenant_in_another_state_than_Active_stays_where_it_is(TenantState state)
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), Payload(), clock);
        await SetStateAsync(customer.TenantId, state);
        await using var host = DispatcherHost(clock, probe);

        clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.Equal((IntegrationMessageState.Pending, 0), ((await MessageAsync(id)).State, (await MessageAsync(id)).Attempts));
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task A_tenant_suspended_after_the_listing_holds_the_claim()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), Payload(), clock);

        var hook = new SuspendWhen(this.database.ConnectionString, customer.TenantId, (command, context) => context?.RuntimeContext is null && command.CommandText.Contains("FROM [OutgoingMessages]", StringComparison.Ordinal));
        await using var host = DispatcherHost(clock, probe, interceptors: [hook]);
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 1), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.True(hook.Fired);
        var held = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Pending, 0, (Guid?)null), (held.State, held.Attempts, held.ClaimToken));
        Assert.Empty(probe.Calls);

        await SetStateAsync(customer.TenantId, TenantState.Active);
        Assert.Equal(1, (await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct)).Delivered);
    }

    [Fact]
    public async Task A_tenant_suspended_after_the_claim_holds_the_delivery_and_the_attempt_is_given_back()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);

        var hook = new SuspendBeforeDelivery(this.database.ConnectionString, customer.TenantId);
        await using var host = DispatcherHost(clock, probe, interceptors: [hook]);
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 1), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.True(hook.Fired);
        var held = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Pending, 0, (DateTime?)null), (held.State, held.Attempts, held.ClaimedUntil));
        Assert.Empty(probe.Calls);
        Assert.False(await ReceiptExistsAsync(id));

        await SetStateAsync(customer.TenantId, TenantState.Active);
        Assert.Equal(1, (await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct)).Delivered);
        Assert.Equal((1, 1), ((await MessageAsync(id)).Attempts, await FolderCountAsync(payload)));
    }

    [Fact]
    public async Task A_correlations_messages_go_in_the_order_they_were_written_and_a_later_one_waits_for_an_earlier_ones_retry()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var (job, other) = (Guid.NewGuid(), Guid.NewGuid());
        var first = await SendAsync(company, customer.TenantId, job, Payload(), clock);
        var second = await SendAsync(company, customer.TenantId, job, Payload(), clock);
        var unrelated = await SendAsync(company, customer.TenantId, other, Payload(), clock);
        var third = await SendAsync(company, customer.TenantId, job, Payload(), clock);
        probe.FailTimes(first, 1);
        await using var host = DispatcherHost(clock, probe);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        Assert.Equal(new IntegrationOutboxPass(1, 1, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal([first, unrelated], probe.Calls);
        Assert.Equal(IntegrationMessageState.Pending, (await MessageAsync(second)).State);

        clock.Advance(IntegrationRetryRules.FirstRetry);
        Assert.Equal(new IntegrationOutboxPass(3, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal([first, unrelated, first, second, third], probe.Calls);
    }

    [Fact]
    public async Task A_dispatcher_whose_claim_lapsed_writes_nothing_once_another_delivered_the_message()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        var entered = probe.Gate(id);
        await using var stalling = DispatcherHost(clock, probe);
        await using var other = DispatcherHost(clock, probe);

        var stalled = stalling.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct);
        await entered.WaitAsync(Ct);

        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await other.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));

        clock.Advance(IntegrationRetryRules.ClaimLease);
        Assert.Equal(new IntegrationOutboxPass(1, 0, 0, 0), await other.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));

        probe.Release(id);
        Assert.Equal(new IntegrationOutboxPass(0, 0, 0, 0), await stalled);
        Assert.Equal([id, id], probe.Calls);
        Assert.Equal(1, await FolderCountAsync(payload));
        await using var check = this.database.CreateSystemContext();
        Assert.Equal(1, await check.IntegrationReceipts.CountAsync(receipt => receipt.MessageId == id, Ct));
        var message = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Delivered, 2, (string?)null), (message.State, message.Attempts, message.LastFailure));
    }

    [Fact]
    public async Task Two_dispatchers_at_once_deliver_every_message_once_and_each_correlation_in_order()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        Guid[] correlations = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var sent = new List<(Guid Id, Guid Correlation, string Payload)>();
        for (var index = 0; index < 20; index++)
        {
            var payload = Payload();
            var correlation = correlations[index % correlations.Length];
            sent.Add((await SendAsync(company, customer.TenantId, correlation, payload, clock), correlation, payload));
        }

        await using var first = DispatcherHost(clock, probe);
        await using var second = DispatcherHost(clock, probe);
        var delivered = 0;
        for (var round = 0; round < 40 && delivered < sent.Count; round++)
        {
            var passes = await Task.WhenAll(first.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct), second.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
            var thisRound = passes.Sum(pass => pass.Delivered);
            delivered += thisRound;
            if (thisRound == 0)
            {
                clock.Advance(IntegrationRetryRules.LongestRetry);
            }
        }

        Assert.Equal(sent.Count, delivered);
        await using var check = this.database.CreateSystemContext();
        foreach (var (id, _, payload) in sent)
        {
            Assert.Equal(IntegrationMessageState.Delivered, (await MessageAsync(id)).State);
            Assert.Equal(1, await FolderCountAsync(payload));
            Assert.Equal(1, await check.IntegrationReceipts.CountAsync(receipt => receipt.MessageId == id, Ct));
        }

        var firstCalls = probe.Calls.Distinct().ToList();
        foreach (var correlation in correlations)
        {
            var written = sent.Where(message => message.Correlation == correlation).Select(message => message.Id).ToList();
            Assert.Equal(written, firstCalls.Where(written.Contains));
        }
    }

    [Fact]
    public async Task A_concurrency_conflict_in_a_handlers_own_writes_is_a_failed_attempt_and_not_a_lost_claim()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        probe.ConflictOnce(id);
        await using var host = DispatcherHost(clock, probe);
        var dispatcher = host.Get<IntegrationOutboxDispatcher>();

        Assert.Equal(new IntegrationOutboxPass(0, 1, 0, 0), await dispatcher.RunOnceAsync(Ct));
        var failed = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Pending, 1, Start.UtcDateTime + IntegrationRetryRules.FirstRetry, (DateTime?)null, nameof(DbUpdateConcurrencyException)), (failed.State, failed.Attempts, failed.NextAttempt, failed.ClaimedUntil, failed.LastFailure));

        clock.Advance(IntegrationRetryRules.FirstRetry);
        Assert.Equal(new IntegrationOutboxPass(1, 0, 0, 0), await dispatcher.RunOnceAsync(Ct));
        Assert.Equal(1, await FolderCountAsync(payload, customer.TenantId));
    }

    [Fact]
    public async Task A_message_between_the_platform_tenant_and_a_tenant_naming_it_as_its_staffing_company_is_a_dead_letter()
    {
        var (company, _) = await LinkedAsync();
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);

        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET ParentTenantId = {platform.TenantId} WHERE Id = {company.TenantId}", Ct);
        }

        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, platform.TenantId, Guid.NewGuid(), payload, clock);
        await using var host = DispatcherHost(clock, probe);

        Assert.Equal(new IntegrationOutboxPass(0, 0, 1, 0), await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct));
        Assert.Equal((IntegrationMessageState.DeadLettered, IntegrationOutboxDispatcher.NotLinkedReason), ((await MessageAsync(id)).State, (await MessageAsync(id)).LastFailure));
        Assert.Empty(probe.Calls);
        Assert.Equal(0, await FolderCountAsync(payload));
    }

    [Fact]
    public async Task A_messages_content_and_a_receipt_are_never_rewritten_and_its_delivery_is_written_by_the_dispatcher_alone()
    {
        var (company, customer) = await LinkedAsync();
        var (clock, probe) = (new ManualClock(Start), new HandlerProbe());
        var payload = Payload();
        var id = await SendAsync(company, customer.TenantId, Guid.NewGuid(), payload, clock);
        await using (var host = DispatcherHost(clock, probe))
        {
            Assert.Equal(1, (await host.Get<IntegrationOutboxDispatcher>().RunOnceAsync(Ct)).Delivered);
        }

        await using (var sender = this.database.CreateContext(company.Runtime(), clock))
        {
            var row = await sender.IntegrationMessages.AsTracking().SingleAsync(message => message.Id == id, Ct);
            row.Payload = "rewritten";
            Assert.Contains("integration message", (await Assert.ThrowsAsync<WorkflowFieldEditException>(() => sender.SaveChangesAsync(Ct))).Message);
            sender.ChangeTracker.Clear();
            Assert.Contains("integration message", (await Assert.ThrowsAsync<WorkflowFieldEditException>(() => sender.IntegrationMessages.Where(message => message.Id == id).ExecuteUpdateAsync(set => set.SetProperty(message => message.Payload, "rewritten"), Ct))).Message);
            Assert.Contains("integration message", (await Assert.ThrowsAsync<WorkflowFieldEditException>(() => sender.IntegrationMessages.Where(message => message.Id == id).ExecuteDeleteAsync(Ct))).Message);

            row = await sender.IntegrationMessages.AsTracking().SingleAsync(message => message.Id == id, Ct);
            row.State = IntegrationMessageState.Pending;
            row.Finished = null;
            Assert.Contains("dispatcher", (await Assert.ThrowsAsync<WorkflowFieldEditException>(() => sender.SaveChangesAsync(Ct))).Message);
            sender.ChangeTracker.Clear();

            foreach (var forged in new Action<IntegrationMessage>[]
            {
                message => message.State = IntegrationMessageState.Delivered,
                message => message.State = IntegrationMessageState.DeadLettered,
                message => message.Attempts = IntegrationRetryRules.MaxAttempts,
                message => (message.ClaimToken, message.ClaimedUntil) = (Guid.NewGuid(), clock.Now.UtcDateTime.AddDays(1)),
                message => message.Finished = clock.Now.UtcDateTime,
                message => message.LastFailure = "forged",
            })
            {
                var queued = IntegrationOutbox.Enqueue(sender, FolderKind, customer.TenantId, Guid.NewGuid(), Payload());
                forged(queued);
                Assert.Contains("new", (await Assert.ThrowsAsync<WorkflowFieldEditException>(() => sender.SaveChangesAsync(Ct))).Message);
                sender.ChangeTracker.Clear();
            }
        }

        await using (var receiver = this.database.CreateContext(customer.Runtime(), clock))
        {
            Assert.Contains("receipt", (await Assert.ThrowsAsync<WorkflowFieldEditException>(() => receiver.IntegrationReceipts.Where(receipt => receipt.MessageId == id).ExecuteUpdateAsync(set => set.SetProperty(receipt => receipt.Kind, "other"), Ct))).Message);
            Assert.Contains("receipt", (await Assert.ThrowsAsync<WorkflowFieldEditException>(() => receiver.IntegrationReceipts.Where(receipt => receipt.MessageId == id).ExecuteDeleteAsync(Ct))).Message);
        }

        var stored = await MessageAsync(id);
        Assert.Equal((IntegrationMessageState.Delivered, payload, 1), (stored.State, stored.Payload, stored.Attempts));
        Assert.Equal(FolderKind, (await ReceiptAsync(id)).Kind);
    }

    private async Task<(ScenarioTenant Company, ScenarioTenant Customer)> LinkedAsync()
    {
        await using (var settle = this.database.CreateSystemContext())
        {
            await settle.Database.ExecuteSqlInterpolatedAsync($"UPDATE IntegrationMessages SET State = {(int)IntegrationMessageState.DeadLettered}, ClaimedUntil = NULL WHERE State = {(int)IntegrationMessageState.Pending}", Ct);
        }

        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        await using var system = this.database.CreateSystemContext();
        await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET ParentTenantId = {scenario.A.TenantId} WHERE Id = {scenario.B.TenantId}", Ct);
        return (scenario.A, scenario.B);
    }

    private async Task<Guid> SendAsync(ScenarioTenant from, Guid to, Guid correlation, string payload, TimeProvider clock)
    {
        await using var context = this.database.CreateContext(from.Runtime(), clock);
        var message = IntegrationOutbox.Enqueue(context, FolderKind, to, correlation, payload);
        await context.SaveChangesAsync(Ct);
        return message.Id;
    }

    private ServiceHost DispatcherHost(ManualClock clock, HandlerProbe probe, Action<IServiceCollection>? configure = null, IReadOnlyList<IInterceptor>? interceptors = null) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
            services.AddSingleton(probe);
            services.AddIntegrationHandler<FolderHandler>(FolderKind);
            configure?.Invoke(services);
        }, interceptors);

    private async Task MoveAsync(ScenarioTenant platform, Guid tenantId, string step)
    {
        await using var host = ServiceHost.Create(this.database, await SessionCircuit.ResolveAsync(this.database, platform.OwnerId));
        var moved = await host.Get<Staffora.Services.Workflow.WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, step, cancellationToken: Ct);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private async Task SetStateAsync(Guid tenantId, TenantState state)
    {
        await using var system = this.database.CreateSystemContext();
        await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)state} WHERE Id = {tenantId}", Ct);
    }

    private async Task<IntegrationMessage> MessageAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: read an integration message");
        return await context.IntegrationMessages.AsNoTracking().SingleAsync(message => message.Id == id, Ct);
    }

    private async Task<IntegrationReceipt> ReceiptAsync(Guid messageId)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: read a receipt");
        return await context.IntegrationReceipts.AsNoTracking().SingleAsync(receipt => receipt.MessageId == messageId, Ct);
    }

    private async Task<bool> ReceiptExistsAsync(Guid messageId)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: look for a receipt");
        return await context.IntegrationReceipts.AnyAsync(receipt => receipt.MessageId == messageId, Ct);
    }

    private async Task<TestFolder> FolderAsync(string name)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: read a handler's folder");
        return await context.TestFolders.AsNoTracking().SingleAsync(folder => folder.Name == name, Ct);
    }

    private async Task<int> FolderCountAsync(string name, Guid? tenantId = null)
    {
        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: count a handler's folders");
        return await context.TestFolders.CountAsync(folder => folder.Name == name && (tenantId == null || folder.TenantId == tenantId), Ct);
    }

    private static string Payload() => $"Folder {Guid.NewGuid():N}";

    public sealed class HandlerProbe
    {
        private readonly ConcurrentQueue<Guid> calls = new();
        private readonly ConcurrentDictionary<Guid, int> failuresLeft = new();
        private readonly ConcurrentDictionary<Guid, (TaskCompletionSource Entered, TaskCompletionSource Released)> gates = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> released = new();
        private readonly ConcurrentDictionary<Guid, Guid> writeInto = new();
        private readonly ConcurrentDictionary<Guid, bool> conflicts = new();

        public IReadOnlyList<Guid> Calls => [.. this.calls];

        public void FailTimes(Guid messageId, int times) => this.failuresLeft[messageId] = times;

        public void ConflictOnce(Guid messageId) => this.conflicts[messageId] = true;

        public Task Gate(Guid messageId)
        {
            var gate = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            this.gates[messageId] = gate;
            this.released[messageId] = gate.Item2;
            return gate.Item1.Task;
        }

        public void Release(Guid messageId) => this.released[messageId].TrySetResult();

        public void WriteInto(Guid messageId, Guid tenantId) => this.writeInto[messageId] = tenantId;

        public async Task CallAsync(Guid messageId, CancellationToken cancellationToken)
        {
            this.calls.Enqueue(messageId);
            if (this.gates.TryRemove(messageId, out var gate))
            {
                gate.Entered.TrySetResult();
                await gate.Released.Task.WaitAsync(cancellationToken);
            }

            if (this.conflicts.TryRemove(messageId, out _))
            {
                throw new DbUpdateConcurrencyException("The test handler's own save lost a concurrency check.");
            }

            if (this.failuresLeft.TryGetValue(messageId, out var left) && left > 0)
            {
                this.failuresLeft[messageId] = left - 1;
                throw new InvalidOperationException("The test handler failed.");
            }
        }

        public Guid? TenantOf(Guid messageId) => this.writeInto.TryGetValue(messageId, out var tenantId) ? tenantId : null;
    }

    public sealed class FolderHandler : IIntegrationHandler
    {
        private readonly HandlerProbe probe;

        public FolderHandler(HandlerProbe probe)
        {
            this.probe = probe;
        }

        public async Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken)
        {
            await this.probe.CallAsync(delivery.MessageId, cancellationToken);
            context.Set<TestFolder>().Add(new TestFolder { Name = delivery.Payload, TenantId = this.probe.TenantOf(delivery.MessageId) ?? Guid.Empty });
        }
    }

    internal sealed class SuspendWhen : DbCommandInterceptor
    {
        private int fired;
        private readonly string connectionString;
        private readonly Guid tenantId;
        private readonly Func<DbCommand, ApplicationDbContext?, bool> when;

        public SuspendWhen(string connectionString, Guid tenantId, Func<DbCommand, ApplicationDbContext?, bool> when)
        {
            this.connectionString = connectionString;
            this.tenantId = tenantId;
            this.when = when;
        }

        public bool Fired => this.fired == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (this.when(command, eventData.Context as ApplicationDbContext) && Interlocked.Exchange(ref this.fired, 1) == 0)
            {
                await SuspendAsync(this.connectionString, this.tenantId, cancellationToken);
            }

            return result;
        }
    }

    internal sealed class SuspendBeforeDelivery : DbTransactionInterceptor
    {
        private int fired;
        private readonly string connectionString;
        private readonly Guid tenantId;

        public SuspendBeforeDelivery(string connectionString, Guid tenantId)
        {
            this.connectionString = connectionString;
            this.tenantId = tenantId;
        }

        public bool Fired => this.fired == 1;

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is ApplicationDbContext { RuntimeContext: { } runtime } && runtime.Current.EffectiveUserId == Staffora.Core.Entities.Identity.SystemAccount.IdFor(this.tenantId) && Interlocked.Exchange(ref this.fired, 1) == 0)
            {
                await SuspendAsync(this.connectionString, this.tenantId, cancellationToken);
            }

            return result;
        }
    }

    private static async Task SuspendAsync(string connectionString, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Tenants SET State = @state WHERE Id = @id";
        command.Parameters.AddWithValue("@state", (int)TenantState.Suspended);
        command.Parameters.AddWithValue("@id", tenantId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class LogRecorder : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries;

        public LogRecorder(ConcurrentQueue<(LogLevel Level, string Message)> entries)
        {
            this.entries = entries;
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            this.entries.Enqueue((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }
}
