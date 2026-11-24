using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class ResourceWorkflowAttribute : WorkflowAttribute
{
    public const string ActivateStep = "resource-activate";

    public const string DeactivateStep = "resource-deactivate";

    public const string ReactivateStep = "resource-reactivate";

    public const string WithdrawStep = "resource-withdraw";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<ResourceState, Resource>();

        workflow.AddState(ResourceState.Registration).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(ResourceState.Active).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(ResourceState.Inactive).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new(ActivateStep, ResourceState.Registration, ResourceState.Active)
        {
            Label = "Activate",
            Description = "The staff member's account is active and they hold a staff role: they can be staffed",
            RequiredPermission = RolePermission.ResourcesManage,
            IsSystemDriven = true,
            SortOrder = 30,
        });

        workflow.AddTransition(new(DeactivateStep, ResourceState.Active, ResourceState.Inactive)
        {
            Label = "Deactivate",
            Description = "The staff member lost their last staff role, or their account was deactivated: they are no longer staffed, but their history is kept",
            RequiredPermission = RolePermission.ResourcesManage,
            IsSystemDriven = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(ReactivateStep, ResourceState.Inactive, ResourceState.Active)
        {
            Label = "Reactivate",
            Description = "The staff member holds a staff role again and their account is active: they can be staffed again",
            RequiredPermission = RolePermission.ResourcesManage,
            IsSystemDriven = true,
            SortOrder = 30,
        });

        workflow.AddTransition(new(WithdrawStep, ResourceState.Registration, ResourceState.Inactive)
        {
            Label = "Withdraw",
            Description = "The staff role was taken away before the staff member's account was ever active",
            RequiredPermission = RolePermission.ResourcesManage,
            IsSystemDriven = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        return workflow;
    }
}
