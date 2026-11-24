using Staffora.Core;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class EditAuthorityTests
{
    [Fact]
    public void The_designated_save_decides_and_without_one_a_single_self_transition_does()
    {
        var designated = Sample(workflow =>
        {
            workflow.AddTransition(new("save", ParentState.Draft, ParentState.Draft) { IsSave = true, RequiredPermission = RolePermission.JobsEdit });
            workflow.AddTransition(new("other-save", ParentState.Draft, ParentState.Draft) { RequiredPermission = RolePermission.JobsView });
            workflow.AddTransition(new("start", ParentState.Draft, ParentState.Active) { RequiredPermission = RolePermission.PerformCoordination });
        });
        Assert.Equal("save", designated.GetEditAuthority(ParentState.Draft)!.Key);
        Assert.False(designated.EditsGoWithItsSteps(ParentState.Draft));

        var undesignated = Sample(workflow =>
        {
            workflow.AddTransition(new("save", ParentState.Draft, ParentState.Draft) { RequiresPlatformOwner = true });
            workflow.AddTransition(new("note", ParentState.Draft, ParentState.Draft) { TakesNoEdits = true });
            workflow.AddTransition(new("touch", ParentState.Draft, ParentState.Draft) { IsSystemDriven = true });
            workflow.AddTransition(new("start", ParentState.Draft, ParentState.Active) { RequiresPlatformOwner = true });
        });
        Assert.Equal("save", undesignated.GetEditAuthority(ParentState.Draft)!.Key);
    }

    [Fact]
    public void A_state_without_one_takes_edits_with_its_steps_only_when_they_all_ask_one_gate()
    {
        var oneGate = Sample(workflow =>
        {
            workflow.AddTransition(new("start", ParentState.Draft, ParentState.Active) { RequiredPermission = RolePermission.JobsEdit });
            workflow.AddTransition(new("cancel", ParentState.Draft, ParentState.Canceled) { RequiredPermission = RolePermission.JobsEdit });
            workflow.AddTransition(new("delete", ParentState.Draft, ParentState.Deleted) { RequiredPermission = RolePermission.JobsView });
            workflow.AddTransition(new("decide", ParentState.Draft, ParentState.Canceled) { Description = "Decide", TakesNoEdits = true, RequiredPermission = RolePermission.PerformCoordination });
        });
        Assert.Null(oneGate.GetEditAuthority(ParentState.Draft));
        Assert.True(oneGate.EditsGoWithItsSteps(ParentState.Draft));

        var twoGates = Sample(workflow =>
        {
            workflow.AddTransition(new("start", ParentState.Draft, ParentState.Active) { RequiredPermission = RolePermission.JobsEdit });
            workflow.AddTransition(new("cancel", ParentState.Draft, ParentState.Canceled) { RequiredPermission = RolePermission.PerformCoordination });
            workflow.AddTransition(new("stamp", ParentState.Active, ParentState.Active) { RequiredPermission = RolePermission.JobsEdit });
            workflow.AddTransition(new("file", ParentState.Active, ParentState.Active) { RequiresPlatformOwner = true });
        });
        Assert.Null(twoGates.GetEditAuthority(ParentState.Draft));
        Assert.False(twoGates.EditsGoWithItsSteps(ParentState.Draft));
        Assert.Null(twoGates.GetEditAuthority(ParentState.Active));
        Assert.False(twoGates.EditsGoWithItsSteps(ParentState.Active));
    }

    [Fact]
    public void Every_application_step_whose_gate_differs_from_its_states_save_is_held_to_the_save_and_no_state_refuses_edits_a_step_would_carry()
    {
        var differs = new List<string>();
        var refusing = new List<string>();
        foreach (var type in typeof(Tenant).Assembly.GetTypes().Where(WorkflowRegistry.HasWorkflow).OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            var workflow = WorkflowRegistry.Default.Get(type);
            foreach (var state in workflow.States)
            {
                var carrying = workflow.Transitions.Where(step => Equals(step.From, state) && !step.IsSave && !step.IsSystemDriven && !step.TakesNoEdits && !step.To.IsDeleteState()).ToList();
                if (workflow.GetEditAuthority(state) is { } authority)
                {
                    differs.AddRange(carrying
                        .Where(step => !ReferenceEquals(step, authority) && (step.RequiredPermission, step.RequiresPlatformOwner) != (authority.RequiredPermission, authority.RequiresPlatformOwner))
                        .Select(step => $"{type.Name}.{step.Key}"));
                }
                else if (carrying.Count > 0 && !workflow.EditsGoWithItsSteps(state))
                {
                    refusing.Add($"{type.Name}.{state}");
                }
            }
        }

        Assert.Equal(
        [
            $"{nameof(Job)}.{JobWorkflowAttribute.HandOverStep}",
            $"{nameof(Job)}.{JobWorkflowAttribute.StartFromRegistrationStep}",
            $"{nameof(Job)}.{JobWorkflowAttribute.StartStep}",
            $"{nameof(Job)}.{JobWorkflowAttribute.ConfirmStep}",
            $"{nameof(Staffora.Core.Entities.Portal.Order)}.{OrderWorkflowAttribute.SendStep}",
            $"{nameof(ResourceAbsence)}.{ResourceAbsenceWorkflowAttribute.ReportStep}",
            $"{nameof(ResourceAbsence)}.{ResourceAbsenceWorkflowAttribute.RegisterStep}",
        ], differs);

        Assert.Equal([$"{nameof(Staffora.Core.Entities.Identity.ApplicationUser)}.{Staffora.Core.Entities.Identity.UserState.Create}"], refusing);
    }

    private static Workflow<ParentState, SampleParent> Sample(Action<Workflow<ParentState, SampleParent>> steps)
    {
        var workflow = new Workflow<ParentState, SampleParent>();
        foreach (var state in Enum.GetValues<ParentState>())
        {
            workflow.AddState(state);
        }

        steps(workflow);
        return workflow;
    }
}
