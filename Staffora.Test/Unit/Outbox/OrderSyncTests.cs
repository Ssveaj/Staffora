using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Integration;

namespace Staffora.Test.Unit.Outbox;

public class OrderSyncTests
{
    [Fact]
    public void One_table_maps_the_customer_visible_job_states_and_leaves_the_staffing_companys_own_out()
    {
        Assert.Equal(
            [
                (JobState.Coordinating, OrderState.Coordinating, OrderState.Coordinating),
                (JobState.Confirmed, OrderState.Confirmed, OrderState.Confirmed),
                (JobState.InProgress, OrderState.InProgress, OrderState.InProgress),
                (JobState.Completed, OrderState.Completed, OrderState.Completed),
                (JobState.CanceledByCustomer, OrderState.CanceledByUs, OrderState.CanceledByUs),
                (JobState.CanceledBySupplier, OrderState.DeclinedBySupplier, OrderState.CanceledBySupplier),
                (JobState.Deleted, OrderState.DeclinedBySupplier, OrderState.DeclinedBySupplier),
            ],
            Enum.GetValues<JobState>().Where(OrderSync.IsCustomerVisible)
                .Select(state => (state, OrderSync.RequestStateFor(state, confirmed: false)!.Value, OrderSync.RequestStateFor(state, confirmed: true) ?? OrderSync.RequestStateFor(state, confirmed: false)!.Value)));

        foreach (var state in new[] { JobState.Create, JobState.Registration, JobState.AwaitingCoordination })
        {
            Assert.False(OrderSync.IsCustomerVisible(state));
            Assert.Null(OrderSync.RequestStateFor(state, confirmed: false));
            Assert.Null(OrderSync.RequestStateFor(state, confirmed: true));
        }

        Assert.True(OrderSync.WasConfirmed(JobState.Confirmed) && OrderSync.WasConfirmed(JobState.InProgress));
        Assert.False(OrderSync.WasConfirmed(JobState.Coordinating) || OrderSync.WasConfirmed(JobState.AwaitingCoordination));
    }

    [Fact]
    public void A_request_moves_only_forward_and_never_out_of_a_final_state()
    {
        var order = new[] { OrderState.Sent, OrderState.Coordinating, OrderState.Confirmed, OrderState.InProgress };
        for (var i = 0; i < order.Length; i++)
        {
            for (var j = 0; j < order.Length; j++)
            {
                Assert.Equal(j > i, OrderSync.MovesForward(order[i], order[j]));
            }

            Assert.All(OrderSync.Final, final => Assert.True(OrderSync.MovesForward(order[i], final)));
        }

        foreach (var final in OrderSync.Final)
        {
            Assert.All(Enum.GetValues<OrderState>(), target => Assert.False(OrderSync.MovesForward(final, target)));
        }

        Assert.All(Enum.GetValues<OrderState>(), target => Assert.False(OrderSync.MovesForward(OrderState.Registration, target)));

        Assert.Equal(OrderSync.Final.Order(), Enum.GetValues<OrderState>().Where(target => OrderSync.MovesForward(OrderState.CancellationSent, target)).Order());
        Assert.All(Enum.GetValues<OrderState>(), from => Assert.False(OrderSync.MovesForward(from, OrderState.CancellationSent)));
    }

    [Fact]
    public void A_sent_requests_cancellation_waits_for_the_answer_of_a_job_that_ended_already()
    {
        Assert.Equal(
            [(OrderState.Registration, OrderState.CanceledByUs), (OrderState.Sent, OrderState.CancellationSent), (OrderState.Coordinating, OrderState.CancellationSent)],
            Enum.GetValues<OrderState>().Where(OrderSync.OrdererCancels).Select(from => (from, OrderSync.CanceledTo(from))));

        Assert.Equal(
            [JobState.Completed, JobState.CanceledByCustomer, JobState.CanceledBySupplier],
            Enum.GetValues<JobState>().Where(OrderSync.AnswersCancellation).Order());
        Assert.All(Enum.GetValues<JobState>().Where(OrderSync.AnswersCancellation), state => Assert.NotNull(OrderSync.RequestStateFor(state, confirmed: true)));
    }

    [Fact]
    public void The_orderer_cancels_until_the_confirmation_and_the_names_show_from_it()
    {
        Assert.Equal([OrderState.Registration, OrderState.Sent, OrderState.Coordinating], Enum.GetValues<OrderState>().Where(OrderSync.OrdererCancels));
        Assert.Equal([OrderState.Sent, OrderState.Coordinating], Enum.GetValues<OrderState>().Where(OrderSync.CancelCrosses));
        Assert.Equal([OrderState.Confirmed, OrderState.InProgress, OrderState.Completed], Enum.GetValues<OrderState>().Where(OrderSync.ShowsStaffMembers));
        Assert.Equal([JobState.Confirmed, JobState.InProgress, JobState.Completed], Enum.GetValues<JobState>().Where(OrderSync.SendsStaffMembers));
    }

    [Fact]
    public void Staff_members_cross_as_names_alone_trimmed_held_sorted_and_counted()
    {
        Assert.Equal(["Ada Moreno", "bo Lind", "Cy"], OrderSync.StaffNames([(" Cy ", null), ("bo", "Lind"), ("Ada", " Moreno "), ("  ", " ")]));
        Assert.Equal(OrderStaff.NameMaxLength, Assert.Single(OrderSync.StaffNames([(new string('a', 150), new string('b', 150))])).Length);
        Assert.Equal(OrderSync.MaxStaffMembers, OrderSync.StaffNames(Enumerable.Range(0, 50).Select(i => ((string?)$"Staff {i:00}", (string?)null))).Count);
    }

    [Fact]
    public void A_job_state_payload_reads_back_as_written_and_always_fits_its_column()
    {
        var written = OrderSync.WriteJobState(new JobStatePayload(JobState.Confirmed, false, ["Ada Moreno"]));
        var read = OrderSync.ReadJobState(written)!;
        Assert.Equal((JobState.Confirmed, false), (read.State, read.Confirmed));
        Assert.Equal(["Ada Moreno"], read.StaffMembers!);
        Assert.Contains("\"Confirmed\"", written, StringComparison.Ordinal);
        Assert.Null(OrderSync.ReadJobState(OrderSync.WriteJobState(new JobStatePayload(JobState.CanceledBySupplier, true, null)))!.StaffMembers);

        var many = Enumerable.Range(0, OrderSync.MaxStaffMembers).Select(i => $"{i:00}{new string('x', OrderStaff.NameMaxLength - 2)}").ToList();
        var full = OrderSync.WriteJobState(new JobStatePayload(JobState.InProgress, true, many));
        Assert.True(full.Length <= IntegrationMessage.MaxPayloadLength);
        var kept = OrderSync.ReadJobState(full)!.StaffMembers!;
        Assert.Equal(many.Take(kept.Count), kept);
        Assert.InRange(kept.Count, 1, many.Count - 1);

        Assert.Null(OrderSync.ReadJobState("not json"));
    }
}
