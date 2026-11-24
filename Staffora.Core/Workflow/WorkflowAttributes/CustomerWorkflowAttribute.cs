using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class CustomerWorkflowAttribute : WorkflowAttribute
{
    public const string AreaContacts = "Contacts";

    public const string SaveDraftStep = "customer-save-draft";

    public const string ActivateStep = "customer-activate";

    public const string DeleteDraftStep = "customer-delete-draft";

    public const string SaveStep = "customer-save";

    public const string DeactivateStep = "customer-deactivate";

    public const string ReactivateStep = "customer-reactivate";

    public const string DeleteStep = "customer-delete";

    public const string EnablePortalStep = "customer-enable-portal";

    public const string PortalNotEnabledReason = "The customer's portal is enabled already";

    public const string KeepsItsPortalReason = "A customer with a portal is kept: its portal stays linked to it";

    public const string OwnTenantReason = "Only a user of this organization changes its customers";

    private const int Back = 10;
    private const int Neutral = 20;
    private const int Forward = 30;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<CustomerState, Customer>();

        workflow.AddState(CustomerState.Create).ConfigureFields(f => Editable(f));
        workflow.AddState(CustomerState.Active).ConfigureFields(f => Editable(f));

        workflow.AddState(CustomerState.Inactive)
            .ConfigureFields(f => Kept(f))
            .Cascade(e => e.Contacts, CustomerContactState.Inactive, CustomerContactState.Create);

        workflow.AddState(CustomerState.Deleted)
            .ConfigureFields(f => Kept(f))
            .Cascade(e => e.Contacts, CustomerContactState.Deleted);

        workflow.AddTransition(new(SaveDraftStep, CustomerState.Create, CustomerState.Create)
        {
            Label = "Save",
            Description = "Save the customer as a draft: its contacts can be added, and nothing is registered for it until it is activated",
            Icon = "save",
            RequiredPermission = RolePermission.CustomersManage,
            SortOrder = Neutral,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<CustomerState, Customer>(e => e.Name));

        workflow.AddTransition(new(ActivateStep, CustomerState.Create, CustomerState.Active)
        {
            Label = "Activate",
            Description = "Put the customer in use: jobs can be registered for it, and its contacts can be activated",
            Icon = "storefront",
            RequiredPermission = RolePermission.CustomersManage,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })
        .AddCondition(new RequiredFieldCondition<CustomerState, Customer>(e => e.Name));

        workflow.AddTransition(new(DeleteDraftStep, CustomerState.Create, CustomerState.Deleted)
        {
            Label = "Delete",
            Description = "Remove the new customer with its contacts, but its history is kept",
            Icon = "delete",
            ConfirmationMessage = "Delete this customer and its contacts? They are removed for good, but their history is kept.",
            RequiredPermission = RolePermission.CustomersManage,
            ShowWhenBlocked = true,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(SaveStep, CustomerState.Active, CustomerState.Active)
        {
            Label = "Save",
            Description = "Save the customer's name and description. It stays in use",
            Icon = "save",
            RequiredPermission = RolePermission.CustomersManage,
            SortOrder = Neutral,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<CustomerState, Customer>(e => e.Name));

        workflow.AddTransition(new(DeactivateStep, CustomerState.Active, CustomerState.Inactive)
        {
            Label = "Deactivate",
            Description = "Take the customer out of use with every active contact: nothing new is registered for it until it is reactivated",
            Icon = "pause_circle",
            ConfirmationMessage = "Deactivate this customer and its active contacts? Nothing new is registered for them until they are reactivated.",
            RequiredPermission = RolePermission.CustomersManage,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(ReactivateStep, CustomerState.Inactive, CustomerState.Active)
        {
            Label = "Reactivate",
            Description = "Put the customer in use again. Its contacts are reactivated one by one",
            Icon = "play_circle",
            RequiredPermission = RolePermission.CustomersManage,
            SortOrder = Forward,
        });

        workflow.AddTransition(new(DeleteStep, CustomerState.Inactive, CustomerState.Deleted)
        {
            Label = "Delete",
            Description = "Remove the customer with its contacts, but their history is kept. Only a customer nothing uses is deleted",
            Icon = "delete",
            ConfirmationMessage = "Delete this customer and its contacts? They are removed for good, but their history is kept.",
            RequiredPermission = RolePermission.CustomersManage,
            ShowWhenBlocked = true,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        })
        .AddCondition(new DelegateCondition<CustomerState, Customer>(KeepsItsPortalReason, customer => customer.LinkedTenantId is null));

        workflow.AddTransition(new(EnablePortalStep, CustomerState.Active, CustomerState.Active)
        {
            Label = "Enable portal",
            Description = "Give the customer its own portal to order staff from: a first customer administrator is created, whose initial password is shown to you once",
            Icon = "add_link",
            RequiredPermission = RolePermission.CustomerPortalEnable,
            TakesNoEdits = true,
            SortOrder = Forward,
        })
        .AddCondition(new DelegateCondition<CustomerState, Customer>(PortalNotEnabledReason, customer => customer.LinkedTenantId is null))
        .AddAction(new DelegateAction<CustomerState, Customer>(context => Provisioner(context).LinkPortal(context.Entity)));

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    private static DelegateCondition<CustomerState, Customer> InOwnTenant() =>
        DelegateCondition<CustomerState, Customer>.FromContext(OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static ICustomerPortalProvisioner Provisioner(WorkflowContext<CustomerState, Customer> context) =>
        context.ServiceProvider?.GetService(typeof(ICustomerPortalProvisioner)) as ICustomerPortalProvisioner
            ?? throw new InvalidOperationException("The enable-portal step runs only where a portal provisioner is registered.");

    private static StateFieldBuilder<Customer> Editable(StateFieldBuilder<Customer> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.Description, FieldAccessFlags.ReadWrite)
        .Area(AreaContacts, FieldAccessFlags.ReadWrite);

    private static StateFieldBuilder<Customer> Kept(StateFieldBuilder<Customer> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Area(AreaContacts, FieldAccessFlags.ReadOnly);
}
