using Staffora.Core.Entities.Resources;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Resources;

namespace Staffora.Test.Unit.Staffing;

public class ResourceLinkerRuleTests
{
    [Theory]
    [InlineData(ResourceState.Registration, true, true, ResourceState.Active)]
    [InlineData(ResourceState.Inactive, true, true, ResourceState.Active)]
    [InlineData(ResourceState.Active, true, true, ResourceState.Active)]
    [InlineData(ResourceState.Registration, true, false, ResourceState.Registration)]
    [InlineData(ResourceState.Active, true, false, ResourceState.Inactive)]
    [InlineData(ResourceState.Inactive, true, false, ResourceState.Inactive)]
    [InlineData(ResourceState.Registration, false, true, ResourceState.Inactive)]
    [InlineData(ResourceState.Active, false, true, ResourceState.Inactive)]
    [InlineData(ResourceState.Inactive, false, true, ResourceState.Inactive)]
    [InlineData(ResourceState.Active, false, false, ResourceState.Inactive)]
    public void The_target_state_follows_the_roles_and_the_users_state(ResourceState current, bool holdsStaffRole, bool userIsActive, ResourceState target)
    {
        Assert.Equal(target, ResourceLinker.TargetState(current, holdsStaffRole, userIsActive));
    }

    [Fact]
    public void Every_move_the_rule_asks_is_one_system_driven_step_of_the_resource_workflow()
    {
        var workflow = WorkflowRegistry.Default.Get<ResourceState, Resource>();
        foreach (var current in Enum.GetValues<ResourceState>())
        {
            foreach (var holds in new[] { true, false })
            {
                foreach (var active in new[] { true, false })
                {
                    var target = ResourceLinker.TargetState(current, holds, active);
                    var step = ResourceLinker.StepBetween(current, target);
                    if (target == current)
                    {
                        Assert.Null(step);
                        continue;
                    }

                    Assert.True(workflow.TryGetTransition(step!, out var transition), step);
                    Assert.Equal((current, target), (transition.From, transition.To));
                    Assert.True(transition.IsSystemDriven, step);
                }
            }
        }

        Assert.All(workflow.Transitions, transition => Assert.True(transition.IsSystemDriven, transition.Key));
        Assert.Equal(
            [ResourceWorkflowAttribute.ActivateStep, ResourceWorkflowAttribute.DeactivateStep, ResourceWorkflowAttribute.ReactivateStep, ResourceWorkflowAttribute.WithdrawStep],
            workflow.Transitions.Select(transition => transition.Key));
    }

    [Fact]
    public void A_move_the_workflow_has_no_step_for_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ResourceLinker.StepBetween(ResourceState.Inactive, ResourceState.Registration));
        Assert.Throws<InvalidOperationException>(() => ResourceLinker.StepBetween(ResourceState.Active, ResourceState.Registration));
    }
}
