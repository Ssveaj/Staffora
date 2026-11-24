using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Data;
using Staffora.Services.Platform;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class RetentionTests : IClassFixture<RealDatabase>
{
    private static readonly DateTimeOffset Start = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public RetentionTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_error_log_rule_removes_in_batches_only_rows_older_than_its_period_and_the_pass_is_remembered()
    {
        var marker = $"retention-{Guid.NewGuid():N}";
        var period = TimeSpan.FromDays(new RetentionOptions().ErrorLogDays);
        await using (var system = this.database.CreateSystemContext())
        {
            for (var index = 0; index < 5; index++)
            {
                system.ErrorLogs.Add(new ErrorLog { Occurred = (Start - period).UtcDateTime.AddSeconds(-1 - index), Level = ErrorLogLevel.Error, Category = "test", Message = $"{marker} old" });
            }

            system.ErrorLogs.Add(new ErrorLog { Occurred = (Start - period).UtcDateTime.AddMinutes(1), Level = ErrorLogLevel.Error, Category = "test", Message = $"{marker} kept" });
            system.ErrorLogs.Add(new ErrorLog { Occurred = Start.UtcDateTime.AddDays(-1), Level = ErrorLogLevel.Warning, Category = "test", Message = $"{marker} kept" });
            await system.SaveChangesAsync(Ct);
        }

        var logged = new List<string>();
        await using var host = RetentionHost(new FakeClock(Start), batchSize: 2, logged);
        var retention = host.Get<Retention>();
        Assert.Null(retention.LastPass);

        var pass = await retention.RunOnceAsync(Ct);

        Assert.Equal([$"{marker} kept", $"{marker} kept"], await ErrorMessagesAsync(marker));
        Assert.True(pass.ErrorLogEntries >= 5, $"{pass.ErrorLogEntries} error-log rows removed");
        Assert.Equal((0, Start), (pass.Failures, pass.Finished));
        Assert.Same(pass, retention.LastPass);
        Assert.Contains(logged, line => line.Contains($"Retention removed {pass.ErrorLogEntries} error-log rows older than 90 days", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains($"The retention pass removed {pass.ErrorLogEntries} error-log rows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_console_reads_the_last_pass_and_a_tenant_user_is_refused()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Retention Reader", Ct);
        await using var host = RetentionHost(new FakeClock(Start));
        var pass = await host.Get<Retention>().RunOnceAsync(Ct);

        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), services => services.AddSingleton(host.Get<Retention>()));
        var read = console.Get<PlatformConsoleService>().LastRetentionPass();
        Assert.True(read.IsSuccess, read.Error);
        Assert.Same(pass, read.Value);

        await using var user = ServiceHost.Create(this.database, tenant.Runtime(), services => services.AddSingleton(host.Get<Retention>()));
        var refused = user.Get<PlatformConsoleService>().LastRetentionPass();
        Assert.Equal((false, PlatformConsoleService.ErrorLogNotPlatformOwner), (refused.IsSuccess, refused.Error));
    }

    [Fact]
    public async Task Read_notices_older_than_their_period_go_only_from_their_own_tenant_and_unread_or_newer_ones_stay()
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        var period = TimeSpan.FromDays(new RetentionOptions().ReadNoticeDays);
        var old = (Start - period).UtcDateTime.AddMinutes(-1);
        var recent = (Start - period).UtcDateTime.AddMinutes(1);
        var created = old.AddDays(-20);
        var second = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Sam", Ct);

        var readLongAgo = await NoticeAsync(scenario.A, created, [old]);
        var unread = await NoticeAsync(scenario.A, created, [null]);
        var readRecently = await NoticeAsync(scenario.A, created, [recent]);
        var halfRead = await NoticeAsync(scenario.A, created, [old, null], second);
        var otherRecent = await NoticeAsync(scenario.B, created, [recent]);
        var otherLongAgo = await NoticeAsync(scenario.B, created, [old]);

        await using var host = RetentionHost(new FakeClock(Start), batchSize: 1);
        var removed = await host.Get<Retention>().RemoveReadNoticesAsync(scenario.A.TenantId, Start.UtcDateTime - period, Options(batchSize: 1), Ct);

        Assert.Equal(2, removed);
        Assert.Equal((0, false, false), await NoticeLeftAsync(readLongAgo));
        Assert.Equal((1, true, true), await NoticeLeftAsync(unread));
        Assert.Equal((1, true, true), await NoticeLeftAsync(readRecently));
        Assert.Equal((1, true, true), await NoticeLeftAsync(halfRead));
        Assert.Equal((1, true, true), await NoticeLeftAsync(otherRecent));
        Assert.Equal((1, true, true), await NoticeLeftAsync(otherLongAgo));

        var pass = await host.Get<Retention>().RunOnceAsync(Ct);

        Assert.True(pass.ReadNotices >= 1);
        Assert.Equal((0, false, false), await NoticeLeftAsync(otherLongAgo));
        Assert.Equal((1, true, true), await NoticeLeftAsync(otherRecent));
        Assert.Equal((1, true, true), await NoticeLeftAsync(unread));
    }

    [Fact]
    public async Task Delivered_integration_messages_older_than_their_period_go_and_pending_dead_or_newer_ones_stay()
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        var period = TimeSpan.FromDays(new RetentionOptions().ProcessedOutboxDays);
        var old = (Start - period).UtcDateTime.AddMinutes(-1);
        var recent = (Start - period).UtcDateTime.AddMinutes(1);

        var deliveredLongAgo = await MessageAsync(scenario.A, scenario.B, IntegrationMessageState.Delivered, old);
        var deliveredRecently = await MessageAsync(scenario.A, scenario.B, IntegrationMessageState.Delivered, recent);
        var pending = await MessageAsync(scenario.A, scenario.B, IntegrationMessageState.Pending, null);
        var dead = await MessageAsync(scenario.A, scenario.B, IntegrationMessageState.DeadLettered, old.AddDays(-30));
        var otherDelivered = await MessageAsync(scenario.B, scenario.A, IntegrationMessageState.Delivered, old);
        var receiptOfDelivered = await ReceiptAsync(scenario.B, deliveredLongAgo, old);
        var receiptOfPending = await ReceiptAsync(scenario.B, pending, old);
        var receiptOfDead = await ReceiptAsync(scenario.B, dead, old);

        await using var host = RetentionHost(new FakeClock(Start), batchSize: 1);
        var removed = await host.Get<Retention>().RemoveProcessedMessagesAsync(scenario.A.TenantId, Start.UtcDateTime - period, Options(batchSize: 1), Ct);

        Assert.Equal(1, removed);
        Assert.Equal([deliveredRecently, pending, dead, otherDelivered], await MessagesLeftAsync([deliveredLongAgo, deliveredRecently, pending, dead, otherDelivered]));
        Assert.Equal([receiptOfDelivered, receiptOfPending, receiptOfDead], await ReceiptsLeftAsync([receiptOfDelivered, receiptOfPending, receiptOfDead]));

        var pass = await host.Get<Retention>().RunOnceAsync(Ct);

        Assert.True(pass.DeliveredMessages >= 1);
        Assert.Equal([deliveredRecently, pending, dead], await MessagesLeftAsync([deliveredLongAgo, deliveredRecently, pending, dead, otherDelivered]));
        Assert.Equal([receiptOfPending, receiptOfDead], await ReceiptsLeftAsync([receiptOfDelivered, receiptOfPending, receiptOfDead]));
    }

    [Fact]
    public async Task A_tenant_past_its_grace_period_keeps_nothing_but_its_Archived_row_and_the_other_tenant_keeps_every_row()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        await SeedAsync(scenario.A, scenario.B.TenantId);
        await SeedAsync(scenario.B, scenario.A.TenantId);
        await SeedCustomFieldsAsync(platform, scenario.A);
        await TwoTenantScenario.GiveIdentityRoleAsync(this.database, scenario.A.OwnerId, "Retention test role", Ct);

        var clock = new FakeClock(Start);
        await MoveTenantAsync(platform, scenario.A.TenantId, "tenant-schedule-deletion", clock);
        var before = await RowsAsync(scenario.A.TenantId);
        var otherBefore = await RowsAsync(scenario.B.TenantId);
        var toPurged = await MessagesToAsync(scenario.A.TenantId);
        Assert.All(before.Where(rows => TenantRowTypes.Contains(rows.Key)), rows => Assert.True(rows.Value > 0, $"{rows.Key} has no row of the tenant to purge"));
        Assert.All(["OrganizationUnitAttribute", "ApplicationUserAttribute", "OrganizationUnitAttributeDefinition", "IdentityUserRole<Guid>"], name => Assert.True(before[name] > 0, $"{name} has no row of the tenant to purge"));
        Assert.True(toPurged > 0);

        clock.Now = Start.AddDays(new RetentionOptions().PendingDeletionGraceDays).AddMinutes(1);
        var logged = new List<string>();
        await using var host = RetentionHost(clock, batchSize: 1, logged);
        var pass = await host.Get<Retention>().RunOnceAsync(Ct);

        Assert.Equal((1, 0), (pass.PurgedTenants, pass.Failures));
        var receiptsPastTheOutboxPeriod = before[nameof(IntegrationReceipt)];
        Assert.Equal(before.Values.Sum() - receiptsPastTheOutboxPeriod + 1 + toPurged, pass.PurgedRows);
        Assert.All(await RowsAsync(scenario.A.TenantId), rows => Assert.True(rows.Value == 0, $"{rows.Key} keeps {rows.Value} rows of the purged tenant"));
        Assert.Equal(0, await MessagesToAsync(scenario.A.TenantId));
        var otherExpected = WithoutReceipts(otherBefore).ToDictionary(rows => rows.Key, rows => rows.Key == nameof(IntegrationMessage) ? rows.Value - toPurged : rows.Value);
        Assert.Equal(otherExpected, WithoutReceipts(await RowsAsync(scenario.B.TenantId)));
        Assert.Equal(TenantState.Archived, (await TenantAsync(scenario.A.TenantId)).State);
        Assert.Equal(TenantState.Active, (await TenantAsync(scenario.B.TenantId)).State);
        Assert.Contains(logged, line => line.Contains($"tenant-purged moved Tenant {scenario.A.TenantId} from PendingDeletion to Archived", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains($"Retention purged tenant {scenario.A.TenantId}", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains($"Retention purge removed {before[nameof(ApplicationUser)]} rows of ApplicationUser from tenant {scenario.A.TenantId}", StringComparison.Ordinal));
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.True(await system.Roles.AnyAsync(role => role.Name == "Retention test role", Ct));
            Assert.True(await system.ApplicationUserAttributeDefinitions.AnyAsync(field => field.TenantId == null && field.Name == GlobalFieldName, Ct));
        }

        var again = await host.Get<Retention>().RunOnceAsync(Ct);
        Assert.Equal((0, 0), (again.PurgedTenants, again.Failures));
    }

    [Fact]
    public async Task A_user_the_impersonation_audit_names_is_kept_with_its_creator_and_the_audit_stays()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Retention Audited", Ct);
        var staff = await TwoTenantScenario.AddUserAsync(this.database, tenant, "Robin", Ct);
        Guid audit;
        await using (var system = this.database.CreateSystemContext())
        {
            var row = new ImpersonationAudit { ImpersonatorId = platform.OwnerId, TargetUserId = staff, TargetTenantId = tenant.TenantId, Started = Start.UtcDateTime.AddDays(-5), Ended = Start.UtcDateTime.AddDays(-5).AddMinutes(3) };
            system.ImpersonationAudits.Add(row);
            await system.SaveChangesAsync(Ct);
            audit = row.Id;
        }

        var clock = new FakeClock(Start);
        await MoveTenantAsync(platform, tenant.TenantId, "tenant-schedule-deletion", clock);
        clock.Now = Start.AddDays(new RetentionOptions().PendingDeletionGraceDays + 1);
        await using var host = RetentionHost(clock);
        var pass = await host.Get<Retention>().RunOnceAsync(Ct);

        Assert.Equal((1, 0), (pass.PurgedTenants, pass.Failures));
        await using var check = this.database.CreateSystemContext();
        Assert.Equal([staff, tenant.SystemAccountId], (await check.Users.Where(user => user.TenantId == tenant.TenantId).Select(user => user.Id).ToListAsync(Ct)).OrderBy(id => id == staff ? 0 : 1));
        Assert.True(await check.ImpersonationAudits.AnyAsync(row => row.Id == audit, Ct));
        Assert.All((await RowsAsync(tenant.TenantId)).Where(rows => rows.Key != nameof(ApplicationUser)), rows => Assert.Equal(0, rows.Value));
        Assert.Equal(TenantState.Archived, (await TenantAsync(tenant.TenantId)).State);
    }

    [Fact]
    public async Task A_tenant_within_its_grace_period_or_not_scheduled_for_deletion_is_untouched()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        var grace = TimeSpan.FromDays(new RetentionOptions().PendingDeletionGraceDays);
        var withinGrace = await TwoTenantScenario.AddTenantAsync(this.database, "Retention Within Grace", Ct);
        var suspended = await TwoTenantScenario.AddTenantAsync(this.database, "Retention Suspended", Ct);
        var unrecorded = await TwoTenantScenario.AddTenantAsync(this.database, "Retention Unrecorded", Ct);
        var cancelled = await TwoTenantScenario.AddTenantAsync(this.database, "Retention Cancelled", Ct);
        var active = await TwoTenantScenario.AddTenantAsync(this.database, "Retention Active", Ct);
        var tenants = new[] { withinGrace, suspended, unrecorded, cancelled, active };
        foreach (var tenant in tenants)
        {
            await SeedAsync(tenant, platform.TenantId);
        }

        var clock = new FakeClock(Start);
        await MoveTenantAsync(platform, suspended.TenantId, "tenant-suspend", clock);
        await MoveTenantAsync(platform, cancelled.TenantId, "tenant-schedule-deletion", clock);
        await MoveTenantAsync(platform, cancelled.TenantId, "tenant-cancel-deletion", clock);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)TenantState.PendingDeletion} WHERE Id = {unrecorded.TenantId}", Ct);
        }

        clock.Now = Start.AddDays(400);
        await MoveTenantAsync(platform, withinGrace.TenantId, "tenant-schedule-deletion", clock);
        var before = new List<IReadOnlyDictionary<string, int>>();
        foreach (var tenant in tenants)
        {
            before.Add(WithoutReceipts(await RowsAsync(tenant.TenantId)));
        }

        clock.Now = Start.AddDays(400) + grace - TimeSpan.FromMinutes(1);
        await using var host = RetentionHost(clock);
        var pass = await host.Get<Retention>().RunOnceAsync(Ct);

        Assert.Equal(0, pass.Failures);
        for (var index = 0; index < tenants.Length; index++)
        {
            Assert.Equal(before[index], WithoutReceipts(await RowsAsync(tenants[index].TenantId)));
        }

        var states = new List<TenantState>();
        foreach (var tenant in tenants)
        {
            states.Add((await TenantAsync(tenant.TenantId)).State);
        }

        Assert.Equal([TenantState.PendingDeletion, TenantState.Suspended, TenantState.PendingDeletion, TenantState.Suspended, TenantState.Active], states);
        Assert.Equal(TenantState.Active, (await TenantAsync(platform.TenantId)).State);
    }

    private const string GlobalFieldName = "Retention badge";

    private static readonly HashSet<string> TenantRowTypes = [.. Unit.Data.ModelUnderTest.TenantRows.Select(type => type.DisplayName())];

    private ServiceHost RetentionHost(TimeProvider clock, int batchSize = RetentionOptions.MaxBatchSize, List<string>? logged = null) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
            services.Configure<RetentionOptions>(options => options.BatchSize = batchSize);
            if (logged is not null)
            {
                services.AddLogging(logging => logging.AddProvider(new MessageRecorder(logged)));
            }
        });

    private static RetentionOptions Options(int batchSize) => new() { BatchSize = batchSize };

    private static IReadOnlyDictionary<string, int> WithoutReceipts(IReadOnlyDictionary<string, int> rows) =>
        rows.Where(pair => pair.Key != nameof(IntegrationReceipt)).ToDictionary(pair => pair.Key, pair => pair.Value);

    private async Task MoveTenantAsync(ScenarioTenant platform, Guid tenantId, string step, TimeProvider clock)
    {
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), services => services.AddSingleton(clock));
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, step, cancellationToken: Ct);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private Task SeedAsync(ScenarioTenant tenant, Guid otherTenant) =>
        new ReadSweepTests(this.database).SeedEveryTenantTableAsync(tenant, otherTenant, Ct);

    private async Task SeedCustomFieldsAsync(ScenarioTenant platform, ScenarioTenant tenant)
    {
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, tenant, Ct);
        await using var system = this.database.CreateSystemContext();
        var own = new OrganizationUnitAttributeDefinition { State = CustomFieldState.Active, Name = "Rooms", ValueType = AttributeValueType.Text, TenantId = tenant.TenantId, CreatedById = tenant.SystemAccountId };
        var global = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = GlobalFieldName, ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId };
        system.AddRange(own, global);
        await system.SaveChangesAsync(Ct);
        system.OrganizationUnitAttributes.Add(new OrganizationUnitAttribute { EntityId = rootId, EntityAttributeDefinitionId = own.Id, StringValue = "Twelve" });
        system.ApplicationUserAttributes.Add(new ApplicationUserAttribute { EntityId = tenant.OwnerId, EntityAttributeDefinitionId = global.Id, StringValue = "Blue" });
        await system.SaveChangesAsync(Ct);
    }

    private async Task<IReadOnlyDictionary<string, int>> RowsAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        var rows = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in TenantPurge.PlanFor(system.Model))
        {
            rows[step.EntityType.DisplayName()] = await TenantPurge.CountAsync(system, step, tenantId, Ct);
        }

        return rows;
    }

    private async Task<int> MessagesToAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.IntegrationMessages.CountAsync(message => message.TargetTenantId == tenantId, Ct);
    }

    private async Task<Tenant> TenantAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Tenants.AsNoTracking().SingleAsync(tenant => tenant.Id == tenantId, Ct);
    }

    private async Task<List<string>> ErrorMessagesAsync(string marker)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.ErrorLogs.Where(row => row.Message.StartsWith(marker)).OrderBy(row => row.Occurred).Select(row => row.Message).ToListAsync(Ct);
    }

    private async Task<(Guid Outgoing, Guid Stored)> NoticeAsync(ScenarioTenant tenant, DateTime created, DateTime?[] readAt, Guid? secondReader = null)
    {
        Guid[] readers = [tenant.OwnerId, secondReader ?? Guid.Empty];
        await using var system = this.database.CreateSystemContext();
        var outgoing = new OutgoingMessage
        {
            TenantId = tenant.TenantId,
            CreatedById = tenant.SystemAccountId,
            Created = created,
            MessageType = TestParent.ActivatedMessage,
            Category = MessageCategory.Account,
            EntityType = nameof(TestParent),
            EntityId = Guid.NewGuid(),
            Subject = "Notice",
            Text = "A notice",
            RecipientUserIds = [tenant.OwnerId],
            Status = OutgoingMessageStatus.Delivered,
            Delivered = created,
            RecipientCount = readAt.Length,
        };
        var stored = new StoredMessage
        {
            TenantId = tenant.TenantId,
            CreatedById = tenant.SystemAccountId,
            Created = created,
            OutgoingMessage = outgoing,
            MessageType = outgoing.MessageType,
            Category = outgoing.Category,
            EntityType = outgoing.EntityType,
            EntityId = outgoing.EntityId,
            Subject = outgoing.Subject,
            Text = outgoing.Text,
            Recipients = [.. readAt.Select((read, index) => new MessageRecipient { TenantId = tenant.TenantId, CreatedById = tenant.SystemAccountId, Created = created, UserId = readers[index], IsRead = read is not null, ReadAt = read })],
        };
        system.StoredMessages.Add(stored);
        await system.SaveChangesAsync(Ct);
        return (outgoing.Id, stored.Id);
    }

    private async Task<(int Copies, bool Stored, bool Outgoing)> NoticeLeftAsync((Guid Outgoing, Guid Stored) notice)
    {
        await using var system = this.database.CreateSystemContext();
        return (
            await system.MessageRecipients.CountAsync(copy => copy.StoredMessageId == notice.Stored, Ct),
            await system.StoredMessages.AnyAsync(stored => stored.Id == notice.Stored, Ct),
            await system.OutgoingMessages.AnyAsync(outgoing => outgoing.Id == notice.Outgoing, Ct));
    }

    private async Task<Guid> MessageAsync(ScenarioTenant sender, ScenarioTenant target, IntegrationMessageState state, DateTime? finished)
    {
        await using var system = this.database.CreateSystemContext();
        var message = new IntegrationMessage { TenantId = sender.TenantId, CreatedById = sender.SystemAccountId, Kind = "test.retention", TargetTenantId = target.TenantId, CorrelationId = Guid.NewGuid(), Payload = "{}", NextAttempt = Start.UtcDateTime.AddDays(-90) };
        system.IntegrationMessages.Add(message);
        await system.SaveChangesAsync(Ct);
        if (state != IntegrationMessageState.Pending)
        {
            message.State = state;
            message.Finished = finished;
            message.Attempts = 1;
            message.ModifiedById = sender.SystemAccountId;
            await system.SaveChangesAsync(Ct);
        }

        return message.Id;
    }

    private async Task<Guid> ReceiptAsync(ScenarioTenant receiver, Guid messageId, DateTime created)
    {
        await using var system = this.database.CreateSystemContext();
        var receipt = new IntegrationReceipt { TenantId = receiver.TenantId, CreatedById = receiver.SystemAccountId, Created = created, MessageId = messageId, Kind = "test.retention" };
        system.IntegrationReceipts.Add(receipt);
        await system.SaveChangesAsync(Ct);
        return receipt.Id;
    }

    private async Task<List<Guid>> MessagesLeftAsync(Guid[] ids)
    {
        await using var system = this.database.CreateSystemContext();
        var left = await system.IntegrationMessages.Where(message => ids.Contains(message.Id)).Select(message => message.Id).ToListAsync(Ct);
        return [.. ids.Where(left.Contains)];
    }

    private async Task<List<Guid>> ReceiptsLeftAsync(Guid[] ids)
    {
        await using var system = this.database.CreateSystemContext();
        var left = await system.IntegrationReceipts.Where(receipt => ids.Contains(receipt.Id)).Select(receipt => receipt.Id).ToListAsync(Ct);
        return [.. ids.Where(left.Contains)];
    }

    private sealed class MessageRecorder : ILoggerProvider, ILogger
    {
        private readonly List<string> messages;

        public MessageRecorder(List<string> messages)
        {
            this.messages = messages;
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (this.messages)
            {
                this.messages.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
