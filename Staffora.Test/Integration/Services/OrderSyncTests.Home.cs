using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;
using Staffora.Services.Home;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;

namespace Staffora.Test.Integration.Services;

public sealed partial class OrderSyncTests
{
    [Fact]
    public async Task The_coordinators_home_shows_what_a_portal_sent_and_a_customer_message_until_a_coordinator_replies()
    {
        var s = await this.ScenarioAsync("Home Portal");
        var requestId = await this.SendAsync(s, "Windows for block 8");
        var jobId = await this.JobOfAsync(requestId);

        var home = await this.CoordinatorHomeAsync(s);
        Assert.Equal(new HomeOrders(1, 0), home.Portal!.Counts);
        Assert.Equal([(jobId, JobState.AwaitingCoordination, "Brightpath Learning")], home.Portal.Orders.Select(order => (order.JobId, order.State, order.Customer)));
        Assert.Equal((0, 0), (home.Portal.MessageCount, home.Portal.Messages.Count));

        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await using (var orderer = this.HostOf(s, s.Portal.RuntimeOf(s.OrdererId)))
        {
            var asked = await orderer.Get<OrderService>().AddOrderCommentAsync(requestId, CommentAudience.Customer, "Any news?", Ct);
            Assert.True(asked.IsSuccess, asked.Error);
        }

        await this.DeliverAsync(s);
        home = await this.CoordinatorHomeAsync(s);
        var message = Assert.Single(home.Portal!.Messages);
        Assert.Equal((jobId, "Brightpath Learning", 1), (message.JobId, message.Customer, home.Portal.MessageCount));

        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await using (var coordinator = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator)))
        {
            var replied = await coordinator.Get<JobService>().AddJobCommentAsync(jobId, CommentAudience.Customer, "Tomorrow.", Ct);
            Assert.True(replied.IsSuccess, replied.Error);
        }

        home = await this.CoordinatorHomeAsync(s);
        Assert.Equal((0, 0), (home.Portal!.MessageCount, home.Portal.Messages.Count));
        Assert.Single(home.Portal.Orders);
    }

    private async Task<CoordinatorHome> CoordinatorHomeAsync(SyncScenario s)
    {
        await using var host = this.HostOf(s, s.Staffing.StaffMembers.Onboarded.RuntimeOf(s.Coordinator));
        var home = await host.Get<TenantHomeService>().ReadCoordinatorAsync(Ct);
        Assert.True(home.IsSuccess, home.Error);
        return home.Value!;
    }
}
