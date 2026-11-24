using Staffora.Core.Entities.Comments;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;

namespace Staffora.Test.Integration.Services;

public sealed partial class OrderSyncTests
{
    [Fact]
    public async Task The_dock_lists_an_order_a_portal_sent_and_a_customer_message_until_a_coordinator_replies()
    {
        var s = await this.ScenarioAsync("Dock Portal");
        var requestId = await this.SendAsync(s, "Windows for block 9");
        var jobId = await this.JobOfAsync(requestId);

        s.Clock.Advance(TimeSpan.FromHours(5));
        var board = await this.PrioritiesAsync(s);
        var order = Assert.Single(board.Items, item => item.Kind == PriorityItemKind.OrdersToAnswer);
        Assert.Equal(("Brightpath Learning", PriorityTarget.Job, jobId, "Waited 5 h"), (order.Title, order.Target, order.TargetId, order.Meta));
        Assert.StartsWith("Windows for block 9", order.Text, StringComparison.Ordinal);
        Assert.Equal(1, board.CountOf(PriorityTab.Orders));
        Assert.DoesNotContain(board.Items, item => item.Kind == PriorityItemKind.CustomerMessages);

        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            var asked = await orderer.Get<OrderService>().AddOrderCommentAsync(requestId, CommentAudience.Customer, "Any news?", Ct);
            Assert.True(asked.IsSuccess, asked.Error);
        }

        await this.DeliverAsync(s);
        board = await this.PrioritiesAsync(s);
        var message = Assert.Single(board.Items, item => item.Kind == PriorityItemKind.CustomerMessages);
        Assert.Equal(("Brightpath Learning", PriorityTarget.Job, jobId), (message.Title, message.Target, message.TargetId));
        Assert.Equal(1, board.CountOf(PriorityTab.Questions));

        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var replied = await coordinator.Get<JobService>().AddJobCommentAsync(jobId, CommentAudience.Customer, "Tomorrow.", Ct);
            Assert.True(replied.IsSuccess, replied.Error);
        }

        board = await this.PrioritiesAsync(s);
        Assert.DoesNotContain(board.Items, item => item.Kind == PriorityItemKind.CustomerMessages);
        Assert.Single(board.Items, item => item.Kind == PriorityItemKind.OrdersToAnswer);
    }

    private async Task<PriorityBoard> PrioritiesAsync(SyncScenario s)
    {
        await using var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator));
        var coordination = host.Get<CoordinationService>();
        var snapshot = await coordination.ReadAsync(Ct);
        Assert.True(snapshot.IsSuccess, snapshot.Error);
        var board = await coordination.ReadPrioritiesAsync(snapshot.Value!, new PriorityQuery(), Ct);
        Assert.True(board.IsSuccess, board.Error);
        return board.Value!;
    }
}
