using Staffora.Core.Entities.Resources;
using Staffora.Core.Workflow;
using Staffora.Services.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class SystemStepProblemTests
{
    [Fact]
    public void A_users_step_does_not_run_inside_another_write()
    {
        var workflow = Environments.Parent;
        var activate = workflow.GetTransition("parent-activate");

        Assert.False(activate.IsSystemDriven);
        Assert.Empty(((IWorkflow)workflow).GetCascades(activate.Key));
        Assert.Equal("is a user's step, not a system-driven one", WorkflowService.SystemStepProblem(workflow, activate));
    }

    [Fact]
    public void A_system_driven_step_that_takes_child_records_along_does_not_run_inside_another_write()
    {
        var workflow = Environments.Parent;
        var syncCancel = workflow.GetTransition("parent-sync-cancel");

        Assert.True(syncCancel.IsSystemDriven);
        Assert.NotEmpty(((IWorkflow)workflow).GetCascades(syncCancel.Key));
        Assert.Equal("takes child records along", WorkflowService.SystemStepProblem(workflow, syncCancel));
    }

    [Fact]
    public void A_system_driven_step_into_a_delete_state_does_not_run_inside_another_write_and_a_plain_one_does()
    {
        var workflow = new Workflow<GrandchildState, SampleGrandchild>();
        workflow.AddState(GrandchildState.Open);
        workflow.AddState(GrandchildState.Canceled);
        workflow.AddState(GrandchildState.Deleted);
        var purge = workflow.AddTransition(new("grandchild-purge", GrandchildState.Open, GrandchildState.Deleted) { Description = "Removed by the other side", IsSystemDriven = true });
        var close = workflow.AddTransition(new("grandchild-close", GrandchildState.Open, GrandchildState.Canceled) { Description = "Closed by the other side", IsSystemDriven = true });

        Assert.Empty(((IWorkflow)workflow).GetCascades(purge.Key));
        Assert.Equal("leads into a delete state", WorkflowService.SystemStepProblem(workflow, purge));
        Assert.Null(WorkflowService.SystemStepProblem(workflow, close));
    }

    [Fact]
    public void Every_step_of_a_staff_record_runs_inside_another_write()
    {
        var workflow = WorkflowRegistry.Default.Get<ResourceState, Resource>();

        Assert.All(workflow.Transitions, transition => Assert.Null(WorkflowService.SystemStepProblem(workflow, transition)));
    }
}
