using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class CustomerWorkflowTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Workflow<CustomerState, Customer> Customers => WorkflowRegistry.Default.Get<CustomerState, Customer>();

    private static Workflow<CustomerContactState, CustomerContact> Contacts => WorkflowRegistry.Default.Get<CustomerContactState, CustomerContact>();

    [Fact]
    public void The_lifecycles_are_new_active_inactive_and_deleted_with_their_stored_values()
    {
        Assert.Equal([(CustomerState.Create, 0), (CustomerState.Active, 1), (CustomerState.Inactive, 2), (CustomerState.Deleted, 3)], Enum.GetValues<CustomerState>().Select(state => (state, (int)state)));
        Assert.Equal([(CustomerContactState.Create, 0), (CustomerContactState.Active, 1), (CustomerContactState.Inactive, 2), (CustomerContactState.Deleted, 3)], Enum.GetValues<CustomerContactState>().Select(state => (state, (int)state)));
        Assert.Equal(
            [
                (CustomerWorkflowAttribute.SaveDraftStep, CustomerState.Create, CustomerState.Create),
                (CustomerWorkflowAttribute.ActivateStep, CustomerState.Create, CustomerState.Active),
                (CustomerWorkflowAttribute.DeleteDraftStep, CustomerState.Create, CustomerState.Deleted),
                (CustomerWorkflowAttribute.SaveStep, CustomerState.Active, CustomerState.Active),
                (CustomerWorkflowAttribute.DeactivateStep, CustomerState.Active, CustomerState.Inactive),
                (CustomerWorkflowAttribute.ReactivateStep, CustomerState.Inactive, CustomerState.Active),
                (CustomerWorkflowAttribute.DeleteStep, CustomerState.Inactive, CustomerState.Deleted),
                (CustomerWorkflowAttribute.EnablePortalStep, CustomerState.Active, CustomerState.Active),
            ],
            Customers.Transitions.Select(transition => (transition.Key, transition.From, transition.To)));
        Assert.Equal(
            [
                (CustomerContactWorkflowAttribute.SaveDraftStep, CustomerContactState.Create, CustomerContactState.Create),
                (CustomerContactWorkflowAttribute.ActivateStep, CustomerContactState.Create, CustomerContactState.Active),
                (CustomerContactWorkflowAttribute.DeleteDraftStep, CustomerContactState.Create, CustomerContactState.Deleted),
                (CustomerContactWorkflowAttribute.SaveStep, CustomerContactState.Active, CustomerContactState.Active),
                (CustomerContactWorkflowAttribute.DeactivateStep, CustomerContactState.Active, CustomerContactState.Inactive),
                (CustomerContactWorkflowAttribute.ReactivateStep, CustomerContactState.Inactive, CustomerContactState.Active),
                (CustomerContactWorkflowAttribute.DeleteStep, CustomerContactState.Inactive, CustomerContactState.Deleted),
            ],
            Contacts.Transitions.Select(transition => (transition.Key, transition.From, transition.To)));
        Assert.All(Customers.Transitions.Where(transition => transition.Key != CustomerWorkflowAttribute.EnablePortalStep), transition => Assert.Equal(RolePermission.CustomersManage, transition.RequiredPermission));
        Assert.All(Contacts.Transitions, transition => Assert.Equal(RolePermission.CustomersManage, transition.RequiredPermission));
        Assert.All(Customers.Transitions.Where(transition => transition.To == CustomerState.Deleted), transition => Assert.NotNull(transition.ConfirmationMessage));
        Assert.All(Contacts.Transitions.Where(transition => transition.To == CustomerContactState.Deleted), transition => Assert.NotNull(transition.ConfirmationMessage));
    }

    [Fact]
    public void Enable_portal_asks_CustomerPortalEnable_takes_no_edits_and_is_refused_once_the_portal_exists_and_a_customer_with_a_portal_is_not_deleted()
    {
        var enable = Customers.Transitions.Single(transition => transition.Key == CustomerWorkflowAttribute.EnablePortalStep);
        Assert.Equal((RolePermission.CustomerPortalEnable, true, false, false), (enable.RequiredPermission, enable.TakesNoEdits, enable.IsSave, enable.IsSystemDriven));
        Assert.Null(enable.ConfirmationMessage);

        var environment = InTenant(Tenant);
        var customer = new Customer { TenantId = Tenant, Name = "Brightpath Learning", State = CustomerState.Active };
        Assert.Empty(Customers.GetBlockingReasons(customer, CustomerWorkflowAttribute.EnablePortalStep, environment));

        customer.LinkedTenantId = Guid.NewGuid();
        Assert.Contains(CustomerWorkflowAttribute.PortalNotEnabledReason, Customers.GetBlockingReasons(customer, CustomerWorkflowAttribute.EnablePortalStep, environment));

        customer.State = CustomerState.Inactive;
        Assert.Contains(CustomerWorkflowAttribute.KeepsItsPortalReason, Customers.GetBlockingReasons(customer, CustomerWorkflowAttribute.DeleteStep, environment));
        customer.LinkedTenantId = null;
        Assert.DoesNotContain(CustomerWorkflowAttribute.KeepsItsPortalReason, Customers.GetBlockingReasons(customer, CustomerWorkflowAttribute.DeleteStep, environment));
    }

    [Fact]
    public void A_deactivation_takes_the_active_contacts_along_and_a_delete_takes_every_contact()
    {
        IWorkflow customers = Customers;
        var deactivate = Assert.Single(customers.GetCascades(CustomerWorkflowAttribute.DeactivateStep));
        var delete = Assert.Single(customers.GetCascades(CustomerWorkflowAttribute.DeleteStep));
        Assert.Single(customers.GetCascades(CustomerWorkflowAttribute.DeleteDraftStep));
        Assert.Empty(customers.GetCascades(CustomerWorkflowAttribute.SaveStep));
        Assert.Empty(customers.GetCascades(CustomerWorkflowAttribute.ReactivateStep));
        Assert.Equal(nameof(Customer.Contacts), deactivate.Navigation);
        Assert.Equal(nameof(Customer.Contacts), delete.Navigation);
    }

    [Fact]
    public void A_new_and_an_active_record_edit_their_own_fields_an_inactive_one_none_and_the_portal_links_never()
    {
        foreach (var state in new[] { CustomerState.Create, CustomerState.Active })
        {
            var fields = Customers.GetFieldConfig(new Customer { State = state });
            Assert.Equal(FieldAccessFlags.Required, fields.GetFieldAccess(nameof(Customer.Name)));
            Assert.Equal(FieldAccessFlags.ReadWrite, fields.GetFieldAccess(nameof(Customer.Description)));
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Customer.LinkedTenantId)));
            Assert.Equal(FieldAccessFlags.ReadOnly, fields.GetFieldAccess(nameof(Customer.DefaultUnitId)));
        }

        var inactive = Customers.GetFieldConfig(new Customer { State = CustomerState.Inactive });
        Assert.Equal(FieldAccessFlags.ReadOnly, inactive.GetFieldAccess(nameof(Customer.Name)));
        Assert.Equal(FieldAccessFlags.ReadOnly, inactive.GetAreaAccess(CustomerWorkflowAttribute.AreaContacts));
        Assert.Equal(FieldAccessFlags.ReadWrite, Customers.GetFieldConfig(new Customer { State = CustomerState.Active }).GetAreaAccess(CustomerWorkflowAttribute.AreaContacts));

        var contact = Contacts.GetFieldConfig(new CustomerContact { State = CustomerContactState.Active });
        Assert.Equal(FieldAccessFlags.Required, contact.GetFieldAccess(nameof(CustomerContact.FirstName)));
        Assert.Equal(FieldAccessFlags.ReadWrite, contact.GetFieldAccess(nameof(CustomerContact.Email)));
        Assert.Equal(FieldAccessFlags.ReadOnly, contact.GetFieldAccess(nameof(CustomerContact.CustomerId)));
        Assert.Equal(FieldAccessFlags.ReadOnly, contact.GetFieldAccess(nameof(CustomerContact.LinkedUserId)));
        Assert.Equal(FieldAccessFlags.ReadOnly, Contacts.GetFieldConfig(new CustomerContact { State = CustomerContactState.Inactive }).GetFieldAccess(nameof(CustomerContact.FirstName)));
    }

    [Fact]
    public void A_contact_is_put_in_use_only_under_an_active_customer_and_added_to_no_inactive_one()
    {
        var environment = InTenant(Tenant);
        foreach (var (customerState, activates, saves) in new[] { (CustomerState.Create, false, true), (CustomerState.Active, true, true), (CustomerState.Inactive, false, false) })
        {
            var draft = Contact(CustomerContactState.Create, customerState);
            Assert.Equal(activates, Contacts.GetBlockingReasons(draft, CustomerContactWorkflowAttribute.ActivateStep, environment).Count == 0);
            Assert.Equal(saves, Contacts.GetBlockingReasons(draft, CustomerContactWorkflowAttribute.SaveDraftStep, environment).Count == 0);
            Assert.Equal(activates, Contacts.GetBlockingReasons(Contact(CustomerContactState.Inactive, customerState), CustomerContactWorkflowAttribute.ReactivateStep, environment).Count == 0);
        }

        Assert.Equal([CustomerContactWorkflowAttribute.CustomerActiveReason], Contacts.GetBlockingReasons(Contact(CustomerContactState.Create, CustomerState.Create), CustomerContactWorkflowAttribute.ActivateStep, environment));
        Assert.Equal([CustomerContactWorkflowAttribute.CustomerNotInactiveReason], Contacts.GetBlockingReasons(Contact(CustomerContactState.Create, CustomerState.Inactive), CustomerContactWorkflowAttribute.SaveDraftStep, environment));
    }

    [Fact]
    public void Every_step_needs_a_session_inside_the_records_own_tenant()
    {
        var customer = new Customer { Id = Guid.NewGuid(), TenantId = Tenant, State = CustomerState.Active, Name = "Brightpath Learning" };
        Assert.Empty(Customers.GetBlockingReasons(customer, CustomerWorkflowAttribute.DeactivateStep, InTenant(Tenant)));
        Assert.Equal([CustomerWorkflowAttribute.OwnTenantReason], Customers.GetBlockingReasons(customer, CustomerWorkflowAttribute.DeactivateStep, InTenant(Guid.NewGuid())));
        Assert.Equal([CustomerWorkflowAttribute.OwnTenantReason], Contacts.GetBlockingReasons(Contact(CustomerContactState.Active, CustomerState.Active), CustomerContactWorkflowAttribute.DeactivateStep, InTenant(Guid.NewGuid())));
    }

    private static CustomerContact Contact(CustomerContactState state, CustomerState customerState)
    {
        var customer = new Customer { Id = Guid.NewGuid(), TenantId = Tenant, State = customerState, Name = "Brightpath Learning" };
        return new CustomerContact { Id = Guid.NewGuid(), TenantId = Tenant, State = state, CustomerId = customer.Id, Customer = customer, FirstName = "Ada", LastName = "Moreno" };
    }

    private static WorkflowEnvironment InTenant(Guid tenantId) =>
        new(new FakeClock(Environments.Noon), NavigationLoadState.Complete, new Session(new RuntimeExecutionContext(tenantId, Guid.NewGuid())));

    private sealed class Session : IServiceProvider
    {
        private readonly IRuntimeExecutionContext runtime;

        public Session(IRuntimeExecutionContext runtime)
        {
            this.runtime = runtime;
        }

        public object? GetService(Type serviceType) => serviceType == typeof(IRuntimeExecutionContext) ? this.runtime : null;
    }
}
