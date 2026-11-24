using Staffora.Core.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class LockCandidateTests
{
    [Fact]
    public void Enter_actions_contribute_lock_candidates()
    {
        var probe = new ProbeEntity { Id = Guid.NewGuid() };

        var candidates = Environments.Probe.GetAllCandidatesForTransition(probe, "probe-run", Environments.Complete());

        Assert.True(candidates.Contains(probe.TouchedOnEnter));
    }

    [Fact]
    public void Leave_and_transition_actions_contribute_lock_candidates_beside_the_entity_itself()
    {
        var probe = new ProbeEntity { Id = Guid.NewGuid() };

        var candidates = Environments.Probe.GetAllCandidatesForTransition(probe, "probe-run", Environments.Complete());

        Assert.True(candidates.Contains(probe));
        Assert.True(candidates.Contains(probe.TouchedOnLeave));
        Assert.True(candidates.Contains(probe.TouchedByTransition));
        Assert.Equal(4, candidates.Count);
        Assert.Equal([probe], candidates.ByEntityType[typeof(ProbeEntity)]);
        Assert.Equal(3, candidates.ByEntityType[typeof(SampleGrandchild)].Count);
    }

    [Fact]
    public void A_cascade_contributes_its_children_and_grandchildren_as_lock_candidates()
    {
        var parent = SampleTree.Build(children: 2, grandchildrenEach: 2);

        var candidates = Environments.Parent.GetAllCandidatesForTransition(parent, "parent-delete", Environments.Complete());

        Assert.Equal([parent], candidates.ByEntityType[typeof(SampleParent)]);
        Assert.Equal(parent.Children, candidates.ByEntityType[typeof(SampleChild)]);
        Assert.Equal(parent.Children.SelectMany(c => c.Grandchildren), candidates.ByEntityType[typeof(SampleGrandchild)]);
        Assert.Equal(7, candidates.Count);
    }

    [Fact]
    public void A_cascade_leaves_children_already_in_the_target_or_a_left_alone_state_unlocked()
    {
        var parent = SampleTree.Build(children: 3, grandchildrenEach: 1);
        parent.State = ParentState.Active;
        parent.Children[0].State = ChildState.Canceled;
        parent.Children[1].State = ChildState.Declined;

        var candidates = Environments.Parent.GetAllCandidatesForTransition(parent, "parent-cancel", Environments.Complete());

        Assert.Equal([parent.Children[2]], candidates.ByEntityType[typeof(SampleChild)]);
        Assert.Equal(parent.Children[2].Grandchildren, candidates.ByEntityType[typeof(SampleGrandchild)]);
    }

    [Fact]
    public void A_cascade_refuses_when_its_navigation_is_not_loaded()
    {
        var parent = SampleTree.Build();

        var refused = Assert.Throws<TransitionRefusedException>(() =>
            Environments.Parent.GetAllCandidatesForTransition(parent, "parent-delete", Environments.NothingLoaded()));

        Assert.Equal(["SampleParent.Children is not loaded, so its SampleChild children cannot be moved to Deleted."], refused.Reasons);
    }

    [Fact]
    public void A_candidate_named_by_two_actions_is_listed_once()
    {
        var candidates = new LockCandidates();
        var entity = new SampleGrandchild { Id = Guid.NewGuid() };

        candidates.Add(entity);
        candidates.AddRange([entity, entity]);

        Assert.Equal(1, candidates.Count);
    }
}
