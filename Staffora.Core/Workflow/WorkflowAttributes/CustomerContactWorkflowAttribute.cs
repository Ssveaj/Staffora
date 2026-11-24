using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class CustomerContactWorkflowAttribute : WorkflowAttribute
{
    public const string SaveDraftStep = "contact-save-draft";

    public const string ActivateStep = "contact-activate";

    public const string DeleteDraftStep = "contact-delete-draft";

    public const string SaveStep = "contact-save";

    public const string DeactivateStep = "contact-deactivate";

    public const string ReactivateStep = "contact-reactivate";

    public const string DeleteStep = "contact-delete";

    public const string CustomerActiveReason = "The customer is active";

    public const string CustomerNotInactiveReason = "The customer is not inactive (reactivate it first)";

    private const int Back = 10;
    private const int Neutral = 20;
    private const int Forward = 30;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<CustomerContactState, CustomerContact>();

        workflow.AddState(CustomerContactState.Create).ConfigureFields(f => Editable(f));
        workflow.AddState(CustomerContactState.Active).ConfigureFields(f => Editable(f));
        workflow.AddState(CustomerContactState.Inactive).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(CustomerContactState.Deleted).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new(SaveDraftStep, CustomerContactState.Create, CustomerContactState.Create)
        {
            Label = "Save",
            Description = "Save the contact with its customer. It is named by jobs once it is activated",
            Icon = "save",
            RequiredPermission = RolePermission.CustomersManage,
            SortOrder = Neutral,
            IsSave = true,
        })
        .AddCondition(Required(e => e.FirstName))
        .AddCondition(Required(e => e.LastName))
        .AddCondition(CustomerNotInactive());

        workflow.AddTransition(new(ActivateStep, CustomerContactState.Create, CustomerContactState.Active)
        {
            Label = "Activate",
            Description = "Put the contact in use: jobs of its customer can name it. Needs an active customer",
            Icon = "how_to_reg",
            RequiredPermission = RolePermission.CustomersManage,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })
        .AddCondition(Required(e => e.FirstName))
        .AddCondition(Required(e => e.LastName))
        .AddCondition(CustomerActive());

        workflow.AddTransition(new(DeleteDraftStep, CustomerContactState.Create, CustomerContactState.Deleted)
        {
            Label = "Delete",
            Description = "Remove the new contact, but its history is kept",
            Icon = "delete",
            ConfirmationMessage = "Delete this contact? It is removed for good, but its history is kept.",
            RequiredPermission = RolePermission.CustomersManage,
            ShowWhenBlocked = true,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(SaveStep, CustomerContactState.Active, CustomerContactState.Active)
        {
            Label = "Save",
            Description = "Save the contact's name and details. It stays in use",
            Icon = "save",
            RequiredPermission = RolePermission.CustomersManage,
            SortOrder = Neutral,
            IsSave = true,
        })
        .AddCondition(Required(e => e.FirstName))
        .AddCondition(Required(e => e.LastName));

        workflow.AddTransition(new(DeactivateStep, CustomerContactState.Active, CustomerContactState.Inactive)
        {
            Label = "Deactivate",
            Description = "Take the contact out of use: nothing new names it until it is reactivated",
            Icon = "pause_circle",
            ConfirmationMessage = "Deactivate this contact? Nothing new names it until it is reactivated.",
            RequiredPermission = RolePermission.CustomersManage,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new(ReactivateStep, CustomerContactState.Inactive, CustomerContactState.Active)
        {
            Label = "Reactivate",
            Description = "Put the contact in use again. Needs an active customer",
            Icon = "play_circle",
            RequiredPermission = RolePermission.CustomersManage,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })
        .AddCondition(CustomerActive());

        workflow.AddTransition(new(DeleteStep, CustomerContactState.Inactive, CustomerContactState.Deleted)
        {
            Label = "Delete",
            Description = "Remove the contact, but its history is kept. Only a contact nothing uses is deleted",
            Icon = "delete",
            ConfirmationMessage = "Delete this contact? It is removed for good, but its history is kept.",
            RequiredPermission = RolePermission.CustomersManage,
            ShowWhenBlocked = true,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        });

        foreach (var transition in workflow.Transitions)
        {
            transition.AddCondition(InOwnTenant());
        }

        return workflow;
    }

    private static DelegateCondition<CustomerContactState, CustomerContact> InOwnTenant() =>
        DelegateCondition<CustomerContactState, CustomerContact>.FromContext(CustomerWorkflowAttribute.OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static StateFieldBuilder<CustomerContact> Editable(StateFieldBuilder<CustomerContact> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.FirstName, FieldAccessFlags.Required)
        .Field(e => e.LastName, FieldAccessFlags.Required)
        .Field(e => e.Email, FieldAccessFlags.ReadWrite)
        .Field(e => e.Phone, FieldAccessFlags.ReadWrite)
        .Field(e => e.Title, FieldAccessFlags.ReadWrite);

    private static RequiredFieldCondition<CustomerContactState, CustomerContact> Required(System.Linq.Expressions.Expression<Func<CustomerContact, object?>> field) => new(field);

    private static DelegateCondition<CustomerContactState, CustomerContact> CustomerActive() =>
        new(CustomerActiveReason, e => e.Customer is { State: CustomerState.Active } customer && customer.Id == e.CustomerId, nameof(CustomerContact.Customer));

    private static DelegateCondition<CustomerContactState, CustomerContact> CustomerNotInactive() =>
        new(CustomerNotInactiveReason, e => e.Customer is { State: CustomerState.Create or CustomerState.Active } customer && customer.Id == e.CustomerId, nameof(CustomerContact.Customer));
}
