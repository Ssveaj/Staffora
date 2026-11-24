using Staffora.Core.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class WorkflowExecutionTests
{
    [Fact]
    public async Task Wrong_from_state_is_refused()
    {
        var parent = SampleTree.Build();

        var refused = await Assert.ThrowsAsync<TransitionRefusedException>(() =>
            Environments.Parent.ExecuteTransitionAsync(parent, "parent-cancel", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("SampleParent is Draft, but 'parent-cancel' leaves Active.", refused.Reasons);
        Assert.Equal(ParentState.Draft, parent.State);
        Assert.All(parent.Children, child => Assert.Equal(ChildState.Draft, child.State));
        Assert.False(Environments.Parent.CanTransition(parent, "parent-cancel", Environments.Complete()));
    }

    [Fact]
    public async Task Leave_then_transition_then_enter_actions_run_in_order()
    {
        var probe = new ProbeEntity { Id = Guid.NewGuid(), IsInTransition = true };

        await Environments.Probe.ExecuteTransitionAsync(probe, "probe-run", Environments.Complete(), "  why  ", TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "leave: state=Start target=End inTransition=True comment=why",
                "transition: state=Start target=End inTransition=True comment=why",
                "enter: state=Start target=End inTransition=True comment=why",
            ],
            probe.Log);
    }

    [Fact]
    public async Task Execution_sets_the_state_and_clears_IsInTransition_after_the_actions()
    {
        var probe = new ProbeEntity { Id = Guid.NewGuid(), IsInTransition = true };

        await Environments.Probe.ExecuteTransitionAsync(probe, "probe-run", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ProbeState.End, probe.State);
        Assert.False(probe.IsInTransition);
    }

    [Fact]
    public async Task Queued_messages_and_after_commit_work_are_handed_back_not_run()
    {
        var probe = new ProbeEntity { Id = Guid.NewGuid() };

        var outcome = await Environments.Probe.ExecuteTransitionAsync(probe, "probe-run", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken);

        var message = Assert.Single(outcome.OutgoingMessages);
        Assert.Equal("ProbeRan", message.MessageType);
        Assert.Equal(probe.Id, message.EntityId);
        Assert.Single(outcome.AfterCommit);
        Assert.False(probe.AfterCommitRan);

        var step = Assert.Single(outcome.Steps);
        Assert.Equal(new TransitionStep(probe, ProbeState.Start, ProbeState.End, "probe-run"), step);
    }

    [Fact]
    public async Task A_blocked_transition_is_refused_on_execution_with_its_reasons()
    {
        var parent = new SampleParent { Id = Guid.NewGuid() };

        var refused = await Assert.ThrowsAsync<TransitionRefusedException>(() =>
            Environments.Parent.ExecuteTransitionAsync(parent, "parent-activate", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["Name is required", "Has at least one child"], refused.Reasons);
        Assert.Equal(ParentState.Draft, parent.State);
    }

    [Fact]
    public async Task A_transition_that_passes_its_conditions_moves_the_entity()
    {
        var parent = SampleTree.Build();

        await Environments.Parent.ExecuteTransitionAsync(parent, "parent-activate", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ParentState.Active, parent.State);
    }
}
