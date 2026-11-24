using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Customers;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class CustomerLifecycleTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public CustomerLifecycleTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_an_unused_customer_removes_both_contacts()
    {
        var (scenario, coordinator) = await ArrangeAsync("Deleting Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var customers = host.Get<CustomerService>();

        var customer = await CreateCustomerAsync(customers, "Harbor College", CustomerWorkflowAttribute.ActivateStep);
        var first = await AddContactAsync(customers.Contacts, customer, "Cleo", "Hart", CustomerContactWorkflowAttribute.ActivateStep);
        var second = await AddContactAsync(customers.Contacts, customer, "Dev", "Rao", CustomerContactWorkflowAttribute.ActivateStep);
        Assert.Equal(CustomerWorkflowAttribute.DeleteStep, Assert.Single(WorkflowRegistry.Default.Get<CustomerState, Customer>().GetTransitionsFrom(CustomerState.Inactive), step => step.To == CustomerState.Deleted).Key);
        Assert.True((await StepCustomerAsync(customers, customer, CustomerWorkflowAttribute.DeactivateStep)).IsSuccess);
        Assert.All(await StoredContactsAsync(customer), contact => Assert.Equal(CustomerContactState.Inactive, contact.State));
        Assert.Equal(2, await HistoryCountAsync(nameof(CustomerContact), first));
        Assert.Contains(CustomerContactWorkflowAttribute.CustomerActiveReason, (await StepContactAsync(customers.Contacts, first, CustomerContactWorkflowAttribute.ReactivateStep)).Error, StringComparison.Ordinal);
        Assert.Contains(CustomerContactWorkflowAttribute.CustomerNotInactiveReason, (await AddContactResultAsync(customers.Contacts, customer, "Late", "Comer", CustomerContactWorkflowAttribute.SaveDraftStep)).Error, StringComparison.Ordinal);

        Assert.True((await StepCustomerAsync(customers, customer, CustomerWorkflowAttribute.DeleteStep)).IsSuccess);
        Assert.Null(await StoredCustomerAsync(customer));
        Assert.Empty(await StoredContactsAsync(customer));
        await using (var context = this.database.CreateSystemContext())
        {
            var deletes = await context.StateHistoryEntries.AsNoTracking()
                .Where(entry => (entry.EntityId == customer || entry.EntityId == first || entry.EntityId == second) && entry.ToState == (int)CustomerState.Deleted)
                .ToListAsync(Ct);
            Assert.Equal(3, deletes.Count);
            Assert.Single(deletes.Select(entry => entry.CorrelationId).Distinct());
        }

        var draft = await CreateCustomerAsync(customers, "Harbor College draft", CustomerWorkflowAttribute.SaveDraftStep);
        await AddContactAsync(customers.Contacts, draft, "Eli", "Stone", CustomerContactWorkflowAttribute.SaveDraftStep);
        await AddContactAsync(customers.Contacts, draft, "Fay", "Lund", CustomerContactWorkflowAttribute.SaveDraftStep);
        Assert.True((await StepCustomerAsync(customers, draft, CustomerWorkflowAttribute.DeleteDraftStep)).IsSuccess);
        Assert.Null(await StoredCustomerAsync(draft));
        Assert.Empty(await StoredContactsAsync(draft));
    }

    [Fact]
    public async Task A_test_blocker_refuses_a_customer_delete_with_its_reason()
    {
        var (scenario, coordinator) = await ArrangeAsync("Blocking Staffing");
        Guid customer;
        Guid contact;
        await using (var setup = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var customers = setup.Get<CustomerService>();
            customer = await CreateCustomerAsync(customers, "Northwind School", CustomerWorkflowAttribute.ActivateStep);
            contact = await AddContactAsync(customers.Contacts, customer, "Gus", "Lee", CustomerContactWorkflowAttribute.ActivateStep);
            Assert.True((await StepCustomerAsync(customers, customer, CustomerWorkflowAttribute.DeactivateStep)).IsSuccess);
        }

        var onCustomer = new UseBlocker(typeof(Customer), customer, "A job is registered for this customer.");
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator), services => services.AddSingleton<IDeleteBlocker>(onCustomer)))
        {
            var refused = await StepCustomerAsync(host.Get<CustomerService>(), customer, CustomerWorkflowAttribute.DeleteStep);
            Assert.Equal("A job is registered for this customer.", refused.Error);
            var footer = await host.Get<WorkflowService>().GetAvailableTransitionsAsync<CustomerState, Customer>(customer, CustomerWorkflowQueries.WithWorkflowGraph, Ct);
            Assert.Contains("A job is registered for this customer.", footer.Value!.Single(step => step.Transition.Key == CustomerWorkflowAttribute.DeleteStep).BlockingReasons);
        }

        Assert.Equal((CustomerState.Inactive, false), await StateOfAsync(customer));
        Assert.Equal([(CustomerContactState.Inactive, false)], (await StoredContactsAsync(customer)).Select(row => (row.State, row.IsInTransition)));

        var onContact = new UseBlocker(typeof(CustomerContact), contact, "A job names this contact.");
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator), services => services.AddSingleton<IDeleteBlocker>(onContact)))
        {
            Assert.Equal("A job names this contact.", (await StepCustomerAsync(host.Get<CustomerService>(), customer, CustomerWorkflowAttribute.DeleteStep)).Error);
        }

        Assert.NotNull(await StoredCustomerAsync(customer));
        Assert.Single(await StoredContactsAsync(customer));
    }

    [Fact]
    public async Task A_name_is_unique_in_its_tenant_and_free_in_another()
    {
        var (scenario, coordinator) = await ArrangeAsync("Naming Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var customers = host.Get<CustomerService>();
        await CreateCustomerAsync(customers, "Brightpath Learning", CustomerWorkflowAttribute.SaveDraftStep);
        var other = await CreateCustomerAsync(customers, "Riverside Academy", CustomerWorkflowAttribute.SaveDraftStep);

        Assert.Equal(CustomerService.NameTaken, (await customers.CreateCustomerAsync(new Customer { Name = " brightpath LEARNING " }, CustomerWorkflowAttribute.SaveDraftStep, Ct)).Error);
        var updater = (await customers.LoadCustomerUpdaterAsync(other, Ct)).Value!;
        updater.Entity.Name = "Brightpath Learning";
        Assert.Equal(CustomerService.NameTaken, (await customers.UpdateCustomerAndTransitionAsync(updater, CustomerWorkflowAttribute.SaveDraftStep, null, Ct)).Error);
        Assert.Equal(CustomerService.CreateBySaveOrActivate, (await customers.CreateCustomerAsync(new Customer { Name = "Third" }, CustomerWorkflowAttribute.DeleteDraftStep, Ct)).Error);

        var elsewhere = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName("Other Naming Staffing"));
        await using var otherHost = ServiceHost.Create(this.database, elsewhere.Onboarded.OwnerRuntime());
        await CreateCustomerAsync(otherHost.Get<CustomerService>(), "Brightpath Learning", CustomerWorkflowAttribute.SaveDraftStep);
    }

    [Fact]
    public async Task A_reader_of_customers_reads_them_and_changes_nothing_and_anyone_else_reads_nothing()
    {
        var (scenario, coordinator) = await ArrangeAsync("Reading Staffing");
        Guid customer;
        Guid contact;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            customer = await CreateCustomerAsync(keeper.Get<CustomerService>(), "Lakeside School", CustomerWorkflowAttribute.ActivateStep);
            contact = await AddContactAsync(keeper.Get<CustomerContactService>(), customer, "Hal", "Berg", CustomerContactWorkflowAttribute.ActivateStep);
        }

        var reader = await scenario.AddPersonAsync(this.database, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, Ct, RolePermission.CustomersView);
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(reader)))
        {
            var customers = host.Get<CustomerService>();
            Assert.Equal(["Lakeside School"], (await customers.QueryCustomersAsync(new ListQuery(), Ct)).Value!.Rows.Select(row => row.Name));
            Assert.Equal(["Hal Berg"], (await customers.QueryCustomerContactsAsync(customer, new ListQuery(), Ct)).Value!.Rows.Select(row => row.FullName));
            Assert.True((await customers.GetCustomerEditorOptionsAsync(customer, Ct)).Value!.FieldsLocked);
            Assert.True((await customers.Contacts.GetCustomerContactEditorOptionsAsync(contact, Ct)).Value!.FieldsLocked);
            Assert.Equal(CustomerService.NoManage, (await customers.CreateCustomerAsync(new Customer { Name = "Other" }, CustomerWorkflowAttribute.SaveDraftStep, Ct)).Error);
            Assert.Equal(CustomerService.NoManage, (await customers.NewCustomerAsync(Ct)).Error);
            Assert.Equal(CustomerService.NoManage, (await StepCustomerAsync(customers, customer, CustomerWorkflowAttribute.DeactivateStep)).Error);
            Assert.Equal(CustomerService.NoManage, (await AddContactResultAsync(customers.Contacts, customer, "New", "Person", CustomerContactWorkflowAttribute.SaveDraftStep)).Error);
            Assert.Equal(CustomerService.NoManage, (await StepContactAsync(customers.Contacts, contact, CustomerContactWorkflowAttribute.DeactivateStep)).Error);

            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<CustomerState, Customer>(customer, CustomerWorkflowAttribute.DeactivateStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        }

        Assert.Equal((CustomerState.Active, false), await StateOfAsync(customer));
        Assert.Equal(CustomerContactState.Active, (await StoredContactsAsync(customer)).Single().State);

        var stranger = await scenario.AddPersonAsync(this.database, "Stranger", Ct);
        await using var strangerHost = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(stranger));
        var none = strangerHost.Get<CustomerService>();
        Assert.Equal(CustomerService.NoView, (await none.QueryCustomersAsync(new ListQuery(), Ct)).Error);
        Assert.Equal(CustomerService.NoView, (await none.QueryCustomerContactsAsync(customer, new ListQuery(), Ct)).Error);
        Assert.Equal(CustomerService.NoView, (await none.LoadCustomerUpdaterAsync(customer, Ct)).Error);
        Assert.Equal(CustomerService.NoView, (await none.Contacts.LoadCustomerContactUpdaterAsync(contact, Ct)).Error);
    }

    [Fact]
    public async Task Another_tenant_and_the_platform_console_reach_no_customer_or_contact_of_a_tenant()
    {
        var (scenario, coordinator) = await ArrangeAsync("Isolated Staffing");
        Guid customer;
        Guid contact;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            customer = await CreateCustomerAsync(keeper.Get<CustomerService>(), "Meadow School", CustomerWorkflowAttribute.ActivateStep);
            contact = await AddContactAsync(keeper.Get<CustomerContactService>(), customer, "Ivy", "Nash", CustomerContactWorkflowAttribute.SaveDraftStep);
        }

        var other = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName("Other Isolated Staffing"));
        await using (var host = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime()))
        {
            var customers = host.Get<CustomerService>();
            await CreateCustomerAsync(customers, "Own customer", CustomerWorkflowAttribute.SaveDraftStep);
            Assert.Equal(["Own customer"], (await customers.QueryCustomersAsync(new ListQuery(), Ct)).Value!.Rows.Select(row => row.Name));
            Assert.Empty((await customers.QueryCustomerContactsAsync(customer, new ListQuery(), Ct)).Value!.Rows);
            Assert.Equal(CustomerService.NotFound, (await customers.LoadCustomerUpdaterAsync(customer, Ct)).Error);
            Assert.Equal(CustomerContactService.NotFound, (await customers.Contacts.LoadCustomerContactUpdaterAsync(contact, Ct)).Error);
            Assert.Equal(CustomerService.NotFound, (await customers.Contacts.NewCustomerContactAsync(customer, Ct)).Error);
            Assert.False((await AddContactResultAsync(customers.Contacts, customer, "Spy", "Smith", CustomerContactWorkflowAttribute.SaveDraftStep)).IsSuccess);
            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<CustomerState, Customer>(customer, CustomerWorkflowAttribute.DeactivateStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<CustomerContactState, CustomerContact>(contact, CustomerContactWorkflowAttribute.DeleteDraftStep, CustomerContactWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        }

        await using (var console = ServiceHost.Create(this.database, scenario.Onboarded.Platform.PlatformOwnerRuntime()))
        {
            var customers = console.Get<CustomerService>();
            Assert.Empty((await customers.QueryCustomersAsync(new ListQuery(), Ct)).Value!.Rows);
            Assert.Equal(CustomerService.ChangeInOwnTenantOnly, (await customers.NewCustomerAsync(Ct)).Error);
            Assert.Equal(CustomerService.ChangeInOwnTenantOnly, (await customers.Contacts.NewCustomerContactAsync(customer, Ct)).Error);
            Assert.Equal(CustomerService.ChangeInOwnTenantOnly, (await AddContactResultAsync(customers.Contacts, customer, "Plat", "Form", CustomerContactWorkflowAttribute.SaveDraftStep)).Error);
            var reached = await console.Get<WorkflowService>().TransitionEntityAsync<CustomerState, Customer>(customer, CustomerWorkflowAttribute.DeactivateStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Contains(CustomerWorkflowAttribute.OwnTenantReason, reached.Error, StringComparison.Ordinal);
        }

        Assert.Equal((CustomerState.Active, false), await StateOfAsync(customer));
        Assert.Equal(["Ivy Nash"], (await StoredContactsAsync(customer)).Select(row => row.FullName));
    }

    [Fact]
    public async Task Every_customer_and_contact_step_takes_the_tenants_customer_lock()
    {
        var (scenario, coordinator) = await ArrangeAsync("Locking Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var customers = host.Get<CustomerService>();
        var lockName = AppLock.Customers.Resource(scenario.TenantId);

        host.ClearCommandLog();
        var customer = await CreateCustomerAsync(customers, "Willow School", CustomerWorkflowAttribute.ActivateStep);
        Assert.Contains(host.CommandLog, command => command.Contains("sp_getapplock", StringComparison.Ordinal));
        host.ClearCommandLog();
        var contact = await AddContactAsync(customers.Contacts, customer, "Joy", "Park", CustomerContactWorkflowAttribute.SaveDraftStep);
        Assert.Contains(host.CommandLog, command => command.Contains("sp_getapplock", StringComparison.Ordinal));
        host.ClearCommandLog();
        Assert.True((await StepContactAsync(customers.Contacts, contact, CustomerContactWorkflowAttribute.ActivateStep)).IsSuccess);
        Assert.Contains(host.CommandLog, command => command.Contains("sp_getapplock", StringComparison.Ordinal));
        Assert.StartsWith("customers:", lockName, StringComparison.Ordinal);
    }

    private async Task<(StaffScenario Scenario, Guid Coordinator)> ArrangeAsync(string name)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName(name));
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        return (scenario, coordinator);
    }

    private async Task<Guid> CreateCustomerAsync(CustomerService customers, string name, string step)
    {
        var created = await customers.CreateCustomerAsync(new Customer { Name = name }, step, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Guid> AddContactAsync(CustomerContactService contacts, Guid customerId, string firstName, string lastName, string step, string? title = null, string? email = null, string? phone = null)
    {
        var added = await AddContactResultAsync(contacts, customerId, firstName, lastName, step, title, email, phone);
        Assert.True(added.IsSuccess, added.Error);
        return added.Value;
    }

    private async Task<Staffora.Core.Models.Result<Guid>> AddContactResultAsync(CustomerContactService contacts, Guid customerId, string firstName, string lastName, string step, string? title = null, string? email = null, string? phone = null) =>
        await contacts.CreateCustomerContactAsync(new CustomerContact { CustomerId = customerId, FirstName = firstName, LastName = lastName, Title = title, Email = email, Phone = phone }, step, Ct);

    private async Task<Staffora.Core.Models.Result> StepCustomerAsync(CustomerService customers, Guid customerId, string step)
    {
        var updater = await customers.LoadCustomerUpdaterAsync(customerId, Ct);
        return updater.IsSuccess ? await customers.UpdateCustomerAndTransitionAsync(updater.Value!, step, null, Ct) : Staffora.Core.Models.Result.Failure(updater.Error);
    }

    private async Task<Staffora.Core.Models.Result> StepContactAsync(CustomerContactService contacts, Guid contactId, string step)
    {
        var updater = await contacts.LoadCustomerContactUpdaterAsync(contactId, Ct);
        return updater.IsSuccess ? await contacts.UpdateCustomerContactAndTransitionAsync(updater.Value!, step, null, Ct) : Staffora.Core.Models.Result.Failure(updater.Error);
    }

    private async Task<Customer?> StoredCustomerAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Customers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, Ct);
    }

    private async Task<(CustomerState, bool)> StateOfAsync(Guid id)
    {
        var stored = await StoredCustomerAsync(id);
        return (stored!.State, stored.IsInTransition);
    }

    private async Task<List<CustomerContact>> StoredContactsAsync(Guid customerId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.CustomerContacts.AsNoTracking().Where(row => row.CustomerId == customerId).OrderBy(row => row.LastName).ToListAsync(Ct);
    }

    private async Task<int> HistoryCountAsync(string entityType, Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.StateHistoryEntries.CountAsync(entry => entry.EntityType == entityType && entry.EntityId == id, Ct);
    }

    private sealed class UseBlocker : IDeleteBlocker
    {
        private readonly Type entityType;
        private readonly Guid used;
        private readonly string reason;

        public UseBlocker(Type entityType, Guid used, string reason)
        {
            this.entityType = entityType;
            this.used = used;
            this.reason = reason;
        }

        public Type EntityType => this.entityType;

        public IReadOnlyList<Type> Dependents { get; } = [];

        public Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken) =>
            Task.FromResult(entity.Id == this.used ? this.reason : null);
    }
}
