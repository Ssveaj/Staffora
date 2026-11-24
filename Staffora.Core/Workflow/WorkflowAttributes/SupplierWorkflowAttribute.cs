using Staffora.Core.Entities.Portal;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class SupplierWorkflowAttribute : WorkflowAttribute
{
    public const string OpenStep = "supplier-open";

    public const string DeactivateStep = "supplier-deactivate";

    public const string ReactivateStep = "supplier-reactivate";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<SupplierState, Supplier>();

        workflow.AddState(SupplierState.Create).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(SupplierState.Active).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(SupplierState.Inactive).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new(OpenStep, SupplierState.Create, SupplierState.Active)
        {
            Label = "Open",
            Description = "The staffing company enabled the organization's portal: its people order staff from it",
            IsSystemDriven = true,
            SortOrder = 30,
        });

        workflow.AddTransition(new(DeactivateStep, SupplierState.Active, SupplierState.Inactive)
        {
            Label = "Deactivate",
            Description = "The staffing company keeps the organization out of use: it takes no new orders for now",
            IsSystemDriven = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(ReactivateStep, SupplierState.Inactive, SupplierState.Active)
        {
            Label = "Reactivate",
            Description = "The staffing company puts the organization in use again: it takes orders again",
            IsSystemDriven = true,
            SortOrder = 30,
        });

        return workflow;
    }
}
