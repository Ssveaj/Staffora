using Staffora.Core;
using Staffora.Core.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class CascadeTests
{
    [Fact]
    public async Task Deleting_a_parent_moves_its_children_and_grandchildren_to_their_delete_states()
    {
        var parent = SampleTree.Build(children: 2, grandchildrenEach: 2);

        var outcome = await Environments.Parent.ExecuteTransitionAsync(parent, "parent-delete", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(parent.State.IsDeleteState());
        Assert.All(parent.Children, child => Assert.True(child.State.IsDeleteState()));
        Assert.All(parent.Children.SelectMany(c => c.Grandchildren), grandchild => Assert.True(grandchild.State.IsDeleteState()));

        Assert.Equal(7, outcome.Steps.Count);
        Assert.Equal("parent-delete", outcome.Steps[0].TransitionKey);
        Assert.Same(parent, outcome.Steps[0].Entity);
        Assert.Equal(2, outcome.Steps.Count(s => s.TransitionKey == "child-delete"));
        Assert.Equal(4, outcome.Steps.Count(s => s.TransitionKey == "grandchild-delete"));
    }

    [Fact]
    public async Task A_cancel_cascade_leaves_children_in_a_left_alone_state_as_they_are()
    {
        var parent = SampleTree.Build(children: 2, grandchildrenEach: 1);
        parent.State = ParentState.Active;
        parent.Children[0].State = ChildState.Declined;

        await Environments.Parent.ExecuteTransitionAsync(parent, "parent-cancel", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ChildState.Declined, parent.Children[0].State);
        Assert.Equal(GrandchildState.Open, parent.Children[0].Grandchildren[0].State);
        Assert.Equal(ChildState.Canceled, parent.Children[1].State);
        Assert.Equal(GrandchildState.Canceled, parent.Children[1].Grandchildren[0].State);
    }

    [Fact]
    public async Task A_child_condition_refuses_the_whole_cascade()
    {
        var parent = SampleTree.Build();
        parent.Children[1].IsPinned = true;

        var refused = await Assert.ThrowsAsync<TransitionRefusedException>(() =>
            Environments.Parent.ExecuteTransitionAsync(parent, "parent-delete", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["The child is not pinned"], refused.Reasons);
        Assert.NotEqual(ParentState.Deleted, parent.State);
        Assert.All(parent.Children, child => Assert.Equal(ChildState.Draft, child.State));
    }

    [Fact]
    public void A_pinned_child_shows_the_parents_delete_blocked_with_the_childs_reason()
    {
        var parent = SampleTree.Build();
        parent.Children[1].IsPinned = true;

        var reasons = Environments.Parent.GetBlockingReasons(parent, "parent-delete", Environments.Complete());

        Assert.Equal(["The child is not pinned"], reasons);
        Assert.False(Environments.Parent.CanTransition(parent, "parent-delete", Environments.Complete()));
    }

    [Fact]
    public void A_child_without_an_edge_to_the_target_shows_the_parents_delete_blocked()
    {
        var parent = SampleTree.Build();
        parent.Children[0].State = ChildState.Active;

        var reasons = Environments.Parent.GetBlockingReasons(parent, "parent-delete", Environments.Complete());

        Assert.Equal([$"SampleChild {parent.Children[0].Id} is Active and cannot move to Deleted."], reasons);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_cascade_that_reaches_an_entity_still_moving_is_refused_instead_of_recursing(int entitiesInTheCycle)
    {
        var cycle = Enumerable.Range(0, entitiesInTheCycle).Select(_ => new LinkedEntity { Id = Guid.NewGuid() }).ToList();
        for (var i = 0; i < cycle.Count; i++)
        {
            cycle[i].Links.Add(cycle[(i + 1) % cycle.Count]);
        }

        var first = cycle[0];
        var workflow = WorkflowRegistry.Default.Get<LinkState, LinkedEntity>();
        string[] expected = [$"LinkedEntity {first.Id} is reached again by a cascade of its own transition, so the cascade would never end."];

        var reasons = workflow.GetBlockingReasons(first, "linked-delete", Environments.Complete());
        var candidatesRefused = Assert.Throws<TransitionRefusedException>(() =>
            workflow.GetAllCandidatesForTransition(first, "linked-delete", Environments.Complete()));
        var executionRefused = await Assert.ThrowsAsync<TransitionRefusedException>(() =>
            workflow.ExecuteTransitionAsync(first, "linked-delete", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(expected, reasons);
        Assert.Equal(expected, candidatesRefused.Reasons);
        Assert.Equal(expected, executionRefused.Reasons);
        Assert.All(cycle, entity => Assert.Equal(LinkState.Open, entity.State));
    }

    [Fact]
    public async Task An_entity_reached_twice_but_not_in_a_cycle_moves_once()
    {
        var shared = new LinkedEntity { Id = Guid.NewGuid() };
        var root = new LinkedEntity { Id = Guid.NewGuid(), Links = [new LinkedEntity { Id = Guid.NewGuid(), Links = [shared] }, shared] };
        var workflow = WorkflowRegistry.Default.Get<LinkState, LinkedEntity>();

        Assert.Empty(workflow.GetBlockingReasons(root, "linked-delete", Environments.Complete()));
        Assert.Equal(3, workflow.GetAllCandidatesForTransition(root, "linked-delete", Environments.Complete()).Count);

        var outcome = await workflow.ExecuteTransitionAsync(root, "linked-delete", Environments.Complete(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.Steps.Count);
        Assert.Equal(LinkState.Deleted, shared.State);
    }

    [Fact]
    public void A_child_in_a_state_without_an_edge_to_the_target_refuses_the_cascade()
    {
        var parent = SampleTree.Build();
        parent.Children[0].State = ChildState.Active;

        var refused = Assert.Throws<TransitionRefusedException>(() =>
            Environments.Parent.GetAllCandidatesForTransition(parent, "parent-delete", Environments.Complete()));

        Assert.Equal([$"SampleChild {parent.Children[0].Id} is Active and cannot move to Deleted."], refused.Reasons);
    }
}
