using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Messaging;
using Staffora.Data;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public sealed class NoticeStoreProtectionTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public NoticeStoreProtectionTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_delivered_notice_is_never_edited_and_never_deleted_outside_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, storedId, _) = await DeliverAsync(cancellationToken);

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var stored = await writer.StoredMessages.SingleAsync(row => row.Id == storedId, cancellationToken);
            stored.Subject = "Forged";
            var edit = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Contains("a delivered notice is never edited", edit.Message, StringComparison.Ordinal);
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            writer.StoredMessages.Remove(await writer.StoredMessages.SingleAsync(row => row.Id == storedId, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.SaveChangesAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: try to rewrite a delivered notice");
            var stored = await system.StoredMessages.SingleAsync(row => row.Id == storedId && row.TenantId == scenario.A.TenantId, cancellationToken);
            stored.Text = "Forged";
            system.NameEditor(stored, scenario.A.SystemAccountId);
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var outgoing = await writer.OutgoingMessages.SingleAsync(row => row.Id == writer.StoredMessages.Where(stored => stored.Id == storedId).Select(stored => stored.OutgoingMessageId).Single(), cancellationToken);
            (outgoing.Text, outgoing.RecipientUserIds) = ("Forged", [Guid.NewGuid()]);
            var reworded = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Equal([nameof(OutgoingMessage.RecipientUserIds), nameof(OutgoingMessage.Text)], reworded.Fields.Order());
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.OutgoingMessages.Where(row => row.Id == outgoing.Id).ExecuteDeleteAsync(cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.OutgoingMessages.Where(row => row.Id == outgoing.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Link, "/elsewhere"), cancellationToken));
        }

        await using var reader = this.database.CreateSystemContext();
        var after = await reader.StoredMessages.AsNoTracking().SingleAsync(row => row.Id == storedId, cancellationToken);
        Assert.Equal(("Parent is active", "The parent Parent was activated."), (after.Subject, after.Text));
        Assert.Equal("The parent Parent was activated.", (await reader.OutgoingMessages.AsNoTracking().SingleAsync(row => row.Id == after.OutgoingMessageId, cancellationToken)).Text);
    }

    [Fact]
    public async Task A_copy_changes_only_its_read_flag_and_read_time_and_is_never_deleted_outside_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, copyId) = await DeliverAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var copy = await writer.MessageRecipients.SingleAsync(row => row.Id == copyId, cancellationToken);
            copy.UserId = colleague;
            copy.IsRead = true;
            var moved = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.Equal([nameof(MessageRecipient.UserId)], moved.Fields);
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            writer.MessageRecipients.Remove(await writer.MessageRecipients.SingleAsync(row => row.Id == copyId, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => writer.SaveChangesAsync(cancellationToken));
        }

        var readAt = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var copy = await writer.MessageRecipients.SingleAsync(row => row.Id == copyId, cancellationToken);
            (copy.IsRead, copy.ReadAt) = (true, readAt);
            await writer.SaveChangesAsync(cancellationToken);
        }

        await using var reader = this.database.CreateSystemContext();
        var after = await reader.MessageRecipients.AsNoTracking().SingleAsync(row => row.Id == copyId, cancellationToken);
        Assert.Equal((scenario.A.OwnerId, true, (DateTime?)readAt), (after.UserId, after.IsRead, after.ReadAt));
    }

    [Fact]
    public async Task Bulk_statements_never_rewrite_a_notice_and_set_only_a_copys_read_flag_read_time_and_editor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, storedId, copyId) = await DeliverAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var stored = writer.StoredMessages.Where(row => row.Id == storedId);
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => stored.ExecuteUpdateAsync(set => set.SetProperty(row => row.Subject, "Forged"), cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => stored.ExecuteDeleteAsync(cancellationToken));

            var copies = writer.MessageRecipients.Where(row => row.Id == copyId);
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => copies.ExecuteDeleteAsync(cancellationToken));
            var moved = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => copies.ExecuteUpdateAsync(set => set.SetProperty(row => row.UserId, colleague), cancellationToken));
            Assert.Contains("never UserId", moved.Message, StringComparison.Ordinal);

            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => copies.ExecuteUpdateAsync(
                set => set.SetProperty(row => row.IsRead, true).SetProperty(row => row.StoredMessageId, Guid.NewGuid()), cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.StoredMessages.Where(row => row.Id == storedId && row.TenantId == scenario.A.TenantId).ExecuteDeleteAsync(cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.MessageRecipients.Where(row => row.Id == copyId && row.TenantId == scenario.A.TenantId).ExecuteDeleteAsync(cancellationToken));
        }

        await using (var reader = this.database.CreateSystemContext())
        {
            var unchanged = await reader.MessageRecipients.AsNoTracking().SingleAsync(row => row.Id == copyId, cancellationToken);
            Assert.Equal((scenario.A.OwnerId, storedId, false), (unchanged.UserId, unchanged.StoredMessageId, unchanged.IsRead));
            Assert.Equal("Parent is active", (await reader.StoredMessages.AsNoTracking().SingleAsync(row => row.Id == storedId, cancellationToken)).Subject);
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(1, await writer.MessageRecipients.Where(row => row.Id == copyId).ExecuteUpdateAsync(
                set => set.SetProperty(row => row.IsRead, true).SetProperty(row => row.ReadAt, now).SetProperty(row => row.Modified, now).SetProperty(row => row.ModifiedById, scenario.A.OwnerId),
                cancellationToken));
        }
    }

    private async Task<(TwoTenantScenario Scenario, Guid StoredId, Guid CopyId)> DeliverAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        Guid id;
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            using var lease = writer.BeginSystemScope("test: insert a parent as a transition left it");
            var parent = new TestParent { Name = "Parent", State = TestParentState.Draft };
            writer.TestParents.Add(parent);
            await writer.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var activated = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);
            Assert.True(activated.IsSuccess, activated.Error);
        }

        await using var reader = this.database.CreateSystemContext();
        var stored = await reader.StoredMessages.AsNoTracking().Include(row => row.Recipients).SingleAsync(row => row.EntityId == id, cancellationToken);
        return (scenario, stored.Id, Assert.Single(stored.Recipients).Id);
    }
}
