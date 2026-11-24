using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class TransitionTests
{
    [Fact]
    public void A_transition_keeps_its_metadata()
    {
        var activate = Environments.Parent.GetTransition("parent-activate");
        var delete = Environments.Parent.GetTransition("parent-delete");
        var save = Environments.Parent.GetTransition("parent-save");
        var syncCancel = Environments.Parent.GetTransition("parent-sync-cancel");

        Assert.Equal((ParentState.Draft, ParentState.Active), (activate.From, activate.To));
        Assert.Equal("Put the parent in use", activate.Description);
        Assert.Equal(RolePermission.TenantView, activate.RequiredPermission);
        Assert.True(activate.ShowWhenBlocked);
        Assert.Equal(30, activate.SortOrder);
        Assert.Equal(TransitionPlacement.Trailing, activate.Placement);

        Assert.Equal("Delete this parent and everything below it?", delete.ConfirmationMessage);
        Assert.Equal(TransitionPlacement.Leading, delete.Placement);

        Assert.True(save.IsSelfTransition);
        Assert.Equal(("Save", "save"), (save.Label, save.Icon));

        Assert.True(syncCancel.IsSystemDriven);
        Assert.False(activate.IsSystemDriven);
    }

    [Fact]
    public void Transitions_from_a_state_are_ordered_by_SortOrder()
    {
        var keys = Environments.Parent.GetTransitionsFrom(ParentState.Draft).Select(t => t.Key);

        Assert.Equal(["parent-delete", "parent-save", "parent-activate"], keys);
    }

    [Fact]
    public void A_transition_is_found_by_its_durable_key_and_a_missing_key_answers_false()
    {
        Assert.True(Environments.Parent.TryGetTransition("parent-cancel", out var cancel));
        Assert.Equal(ParentState.Canceled, cancel.To);
        Assert.False(Environments.Parent.TryGetTransition("parent-renamed-long-ago", out _));
        Assert.Throws<KeyNotFoundException>(() => Environments.Parent.GetTransition("parent-renamed-long-ago"));
    }

    [Fact]
    public void A_step_into_a_delete_state_is_a_delete_step_and_any_other_step_or_a_missing_key_is_not()
    {
        Assert.True(Environments.Parent.IsDeleteStep("parent-delete"));
        Assert.False(Environments.Parent.IsDeleteStep("parent-save"));
        Assert.False(Environments.Parent.IsDeleteStep("parent-cancel"));
        Assert.False(Environments.Parent.IsDeleteStep("parent-renamed-long-ago"));

        var patterns = WorkflowRegistry.Default.Get<WorkPatternState, WorkPattern>();
        Assert.Equal((true, false), (patterns.IsDeleteStep(WorkPatternWorkflowAttribute.DeleteStep), patterns.IsDeleteStep(WorkPatternWorkflowAttribute.SaveStep)));
        var assignments = WorkflowRegistry.Default.Get<ResourcePatternAssignmentState, ResourcePatternAssignment>();
        Assert.Equal((true, false), (assignments.IsDeleteStep(ResourcePatternAssignmentWorkflowAttribute.DeleteStep), assignments.IsDeleteStep(ResourcePatternAssignmentWorkflowAttribute.SaveStep)));
        var contacts = WorkflowRegistry.Default.Get<CustomerContactState, CustomerContact>();
        Assert.Equal((true, true, false), (contacts.IsDeleteStep(CustomerContactWorkflowAttribute.DeleteDraftStep), contacts.IsDeleteStep(CustomerContactWorkflowAttribute.DeleteStep), contacts.IsDeleteStep(CustomerContactWorkflowAttribute.SaveStep)));
        var jobs = WorkflowRegistry.Default.Get<JobState, Job>();
        Assert.Equal((true, false), (jobs.IsDeleteStep(JobWorkflowAttribute.DeleteStep), jobs.IsDeleteStep(JobWorkflowAttribute.SaveRegistrationStep)));
        var slots = WorkflowRegistry.Default.Get<JobSlotState, JobSlot>();
        Assert.Equal((true, true, false, false), (slots.IsDeleteStep(JobSlotWorkflowAttribute.DeleteWithJobStep), slots.IsDeleteStep(JobSlotWorkflowAttribute.DeleteCanceledWithJobStep), slots.IsDeleteStep(JobSlotWorkflowAttribute.SaveStep), slots.IsDeleteStep(JobSlotWorkflowAttribute.CancelStep)));
    }

    [Fact]
    public void Two_transitions_between_the_same_states_are_told_apart_by_key()
    {
        var keys = Environments.Parent.GetTransitions(ParentState.Active, ParentState.Canceled).Select(t => t.Key);

        Assert.Equal(["parent-cancel", "parent-sync-cancel"], keys);
    }

    [Fact]
    public void The_untyped_view_lists_states_and_transitions()
    {
        IWorkflow workflow = Environments.Parent;

        Assert.Equal(typeof(SampleParent), workflow.EntityType);
        Assert.Equal(typeof(ParentState), workflow.StateType);
        Assert.Equal(Enum.GetValues<ParentState>().Cast<Enum>().Order(), workflow.States.Order());
        Assert.All(workflow.Transitions, transition => Assert.False(string.IsNullOrWhiteSpace(transition.Description)));
    }
}
