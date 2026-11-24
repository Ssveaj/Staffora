using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Services.Messaging;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class MessagingPipelineTests : IClassFixture<RealDatabase>
{
    private const string Activate = "test-parent-activate";

    private readonly RealDatabase database;

    public MessagingPipelineTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_test_transition_leaves_the_recipient_an_unread_message_linked_to_the_history_entry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(colleague));
        Assert.IsType<OutgoingMessageDispatcher>(host.Get<IOutgoingMessageDispatcher>());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, Activate, cancellationToken: cancellationToken);
        Assert.True(result.IsSuccess, result.Error);

        await using var system = this.database.CreateSystemContext();
        var entry = Assert.Single(await system.StateHistoryEntries.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        var outgoing = Assert.Single(await system.OutgoingMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Equal((scenario.A.TenantId, entry.Id, nameof(TestParent), OutgoingMessageStatus.Delivered, 1), (outgoing.TenantId, outgoing.HistoryEntryId, outgoing.EntityType, outgoing.Status, outgoing.RecipientCount));

        var stored = Assert.Single(await system.StoredMessages.AsNoTracking().Include(row => row.Recipients).Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Equal((outgoing.Id, entry.Id, TestParent.ActivatedMessage), (stored.OutgoingMessageId, stored.HistoryEntryId, stored.MessageType));
        Assert.Equal(("Parent is active", "The parent Parent was activated.", $"?dlg=testparent-{id:D}"), (stored.Subject, stored.Text, stored.Link));
        var recipient = Assert.Single(stored.Recipients);
        Assert.Equal((scenario.A.OwnerId, false, (DateTime?)null, scenario.A.TenantId), (recipient.UserId, recipient.IsRead, recipient.ReadAt, recipient.TenantId));

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        Assert.Equal(1, (await owner.Get<NoticeService>().GetUnreadCountAsync(cancellationToken)).Value);
        Assert.Equal(0, (await host.Get<NoticeService>().GetUnreadCountAsync(cancellationToken)).Value);
    }

    [Theory]
    [InlineData(TestParent.FailInAnAction)]
    [InlineData(TestParent.FailAtTheDatabase)]
    public async Task A_rolled_back_transition_leaves_no_message(string failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken, notes: failure);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, Activate, cancellationToken: cancellationToken);
        Assert.False(result.IsSuccess);

        await using var system = this.database.CreateSystemContext();
        Assert.Empty(await system.StateHistoryEntries.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Empty(await system.OutgoingMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Empty(await system.StoredMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Empty(await system.MessageRecipients.AsNoTracking().Where(row => row.TenantId == scenario.A.TenantId).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Recipients_are_the_active_users_of_the_entitys_tenant_named_or_holding_the_permission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Holder", cancellationToken);
        var inactiveHolder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Inactive", cancellationToken);
        var theirHolder = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Theirs", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, RolePermission.UsersEdit);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, inactiveHolder, cancellationToken, RolePermission.UsersEdit);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, theirHolder, cancellationToken, RolePermission.UsersEdit);
        await DeactivateAsync(inactiveHolder, cancellationToken);

        await using var context = this.database.CreateSystemContext();
        using var lease = context.BeginSystemScope("test: resolve a notice's recipients as the platform lease would");
        var recipients = await InAppChannelSender.ResolveRecipientsAsync(
            context,
            scenario.A.TenantId,
            [scenario.A.OwnerId, scenario.B.OwnerId, scenario.A.SystemAccountId, platform.OwnerId, inactiveHolder],
            RolePermission.UsersEdit,
            cancellationToken);

        Assert.Equal(new[] { scenario.A.OwnerId, holder }.Order(), recipients.Order());
    }

    [Fact]
    public async Task A_platform_owners_step_on_the_console_delivers_into_the_entitys_tenant_on_its_behalf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, Activate, cancellationToken: cancellationToken);
        Assert.True(result.IsSuccess, result.Error);

        await using var system = this.database.CreateSystemContext();
        var outgoing = Assert.Single(await system.OutgoingMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        var stored = Assert.Single(await system.StoredMessages.AsNoTracking().Include(row => row.Recipients).Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Equal((OutgoingMessageStatus.Delivered, scenario.A.TenantId, scenario.A.SystemAccountId, scenario.A.SystemAccountId), (outgoing.Status, outgoing.TenantId, outgoing.CreatedById, outgoing.ModifiedById));
        Assert.Equal((scenario.A.TenantId, scenario.A.SystemAccountId), (stored.TenantId, stored.CreatedById));
        var recipient = Assert.Single(stored.Recipients);
        Assert.Equal((scenario.A.OwnerId, scenario.A.SystemAccountId), (recipient.UserId, recipient.CreatedById));
        Assert.Empty(await system.MessageRecipients.AsNoTracking().Where(row => row.TenantId == platform.TenantId).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task A_failed_delivery_is_logged_marked_not_delivered_and_the_step_stands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);
        var log = new ErrorRecorder();

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services =>
        {
            services.RemoveAll<IChannelSender>();
            services.AddScoped<IChannelSender, FailingSender>();
            services.AddLogging(logging => logging.AddProvider(log));
        });
        var result = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, Activate, cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        await using var system = this.database.CreateSystemContext();
        Assert.Equal(TestParentState.Active, (await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken)).State);
        Assert.Equal(OutgoingMessageStatus.Failed, Assert.Single(await system.OutgoingMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken)).Status);
        Assert.Empty(await system.StoredMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Contains(log.Errors, message => message.Contains("is marked not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delivery_run_twice_delivers_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, cancellationToken);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        Assert.True((await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, Activate, cancellationToken: cancellationToken)).IsSuccess);

        await using (var reader = this.database.CreateSystemContext())
        {
            var committed = await reader.OutgoingMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken);
            await host.Get<IOutgoingMessageDispatcher>().DispatchAsync(committed, committed[0].HistoryEntryId, cancellationToken);
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Single(await system.StoredMessages.AsNoTracking().Where(row => row.EntityId == id).ToListAsync(cancellationToken));
        Assert.Single(await system.MessageRecipients.AsNoTracking().Where(row => row.TenantId == scenario.A.TenantId).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Marking_read_touches_only_the_readers_own_unread_copies_in_their_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        foreach (var (tenant, creator) in new[] { (scenario.A, scenario.A.OwnerId), (scenario.A, scenario.A.OwnerId), (scenario.A, colleague), (scenario.B, scenario.B.OwnerId) })
        {
            var parent = await AddParentAsync(tenant, cancellationToken, creator: creator);
            await using var actor = ServiceHost.Create(this.database, tenant.Runtime());
            Assert.True((await actor.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(parent, Activate, cancellationToken: cancellationToken)).IsSuccess);
        }

        Guid colleaguesCopy;
        await using (var reader = this.database.CreateSystemContext())
        {
            colleaguesCopy = (await reader.MessageRecipients.AsNoTracking().SingleAsync(row => row.UserId == colleague, cancellationToken)).Id;
        }

        await using var owner = ServiceHost.Create(this.database, scenario.A.Runtime());
        var notices = owner.Get<NoticeService>();
        Assert.True((await notices.MarkReadAsync(colleaguesCopy, cancellationToken)).IsSuccess);

        Assert.True((await notices.MarkAllReadAsync(cancellationToken)).IsSuccess);

        await using var system = this.database.CreateSystemContext();
        var copies = await system.MessageRecipients.AsNoTracking()
            .Where(row => row.UserId == scenario.A.OwnerId || row.UserId == colleague || row.UserId == scenario.B.OwnerId)
            .ToListAsync(cancellationToken);
        Assert.Equal(4, copies.Count);
        Assert.All(copies.Where(row => row.UserId == scenario.A.OwnerId), row => Assert.Equal((true, true, scenario.A.OwnerId), (row.IsRead, row.ReadAt.HasValue, row.ModifiedById)));
        Assert.All(copies.Where(row => row.UserId != scenario.A.OwnerId), row => Assert.Equal((false, (DateTime?)null), (row.IsRead, row.ReadAt)));
        Assert.Equal(0, (await notices.GetUnreadCountAsync(cancellationToken)).Value);
    }

    [Fact]
    public async Task A_platform_owner_on_the_console_has_no_notices_and_marks_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var notices = host.Get<NoticeService>();
        Assert.False(notices.HasNotices);
        Assert.False(notices.MayMarkRead);
        Assert.False((await notices.GetUnreadCountAsync(cancellationToken)).IsSuccess);
        Assert.False((await notices.MarkAllReadAsync(cancellationToken)).IsSuccess);
        Assert.Null(notices.Subscribe(_ => Task.CompletedTask));
    }

    private async Task<Guid> AddParentAsync(ScenarioTenant tenant, CancellationToken cancellationToken, string? notes = null, Guid? creator = null)
    {
        await using var writer = this.database.CreateContext(creator is { } user ? tenant.RuntimeOf(user) : tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
        var parent = new TestParent { Name = "Parent", Notes = notes, State = TestParentState.Draft };
        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task DeactivateAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        await system.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(set => set.SetProperty(user => user.State, Core.Entities.Identity.UserState.Inactive), cancellationToken);
    }

    private sealed class FailingSender : IChannelSender
    {
        public MessageChannel Channel => MessageChannel.InApp;

        public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The in-app channel is down.");
    }

    private sealed class ErrorRecorder : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> errors = new();

        public IReadOnlyList<string> Errors => [.. this.errors];

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly ErrorRecorder recorder;

            public Logger(ErrorRecorder recorder)
            {
                this.recorder = recorder;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    this.recorder.errors.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
