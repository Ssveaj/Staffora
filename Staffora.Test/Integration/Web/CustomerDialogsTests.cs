using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Customers;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class CustomerDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public CustomerDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Staff_create_a_customer_with_two_contacts_through_the_dialogs()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Dialog Customers Staffing", cancellationToken);
        await using var circuit = await CircuitAsync(coordinator);

        using (var page = circuit.Page())
        {
            var rendered = page.Render(builder => Host<Customers>(builder));
            rendered.WaitForAssertion(() => Assert.Contains("No customer yet", rendered.Markup, StringComparison.Ordinal), Wait);

            ClickOnce(rendered, () => rendered.FindAll("button").Single(button => TextOf(button) == "New customer"));
            WaitForDialog(rendered);
            rendered.Find("[name=customer-name]").Change("Brightpath Learning");
            rendered.WaitForAssertion(() => rendered.Find(".state-entity-dialog button[data-transition='customer-save-draft']").Click(), Wait);
            rendered.WaitForAssertion(() => Assert.Equal("Brightpath Learning", rendered.Find(".state-entity-dialog-header__title").TextContent.Trim()), Wait);
            WaitForDialog(rendered);

            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=contacts]"));
            rendered.WaitForAssertion(() => Assert.Contains("No contact yet", rendered.Markup, StringComparison.Ordinal), Wait);
            foreach (var (first, last, count) in new[] { ("Ada", "Moreno", 1), ("Ben", "Okafor", 2) })
            {
                ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").Single(button => TextOf(button) == "Add contact"));
                rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
                WaitForDialog(rendered);
                rendered.Find("[name=contact-first-name]").Change(first);
                rendered.Find("[name=contact-last-name]").Change(last);
                Assert.Contains("Brightpath Learning", rendered.Find("[data-customer]").TextContent, StringComparison.Ordinal);
                rendered.WaitForAssertion(() => rendered.FindAll(".state-entity-dialog").Last().QuerySelector("button[data-transition='contact-save-draft']")!.Click(), Wait);
                rendered.WaitForAssertion(() => Assert.Equal($"{first} {last}", rendered.FindAll(".state-entity-dialog-header__title").Last().TextContent.Trim()), Wait);
                WaitForDialog(rendered);
                await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
                rendered.WaitForAssertion(() => Assert.Equal(count, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
            }

            ClickOnce(rendered, () => rendered.Find(".state-entity-dialog button[data-transition='customer-activate']"));
            rendered.WaitForAssertion(() => Assert.Equal("Active", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
            WaitForDialog(rendered);
            foreach (var name in new[] { "Ada Moreno", "Ben Okafor" })
            {
                ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains(name, StringComparison.Ordinal)).QuerySelector("button.list-row__open")!);
                rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<CustomerContactDialog>()), Wait);
                WaitForDialog(rendered);
                Assert.Equal(name, rendered.FindAll(".state-entity-dialog-header__title").Last().TextContent.Trim());
                ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog").Last().QuerySelector("button[data-transition='contact-activate']")!);
                rendered.WaitForAssertion(() => Assert.Equal("Active", rendered.FindAll(".state-pill__button").Last().GetAttribute("data-state")), Wait);
                await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
                rendered.WaitForAssertion(() => Assert.Empty(rendered.FindComponents<CustomerContactDialog>()), Wait);

                rendered.WaitForAssertion(() => Assert.Equal("Active", rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains(name, StringComparison.Ordinal)).QuerySelector(".list-state")!.TextContent.Trim()), Wait);
            }
        }

        var customer = await StoredCustomerAsync(scenario.TenantId, cancellationToken);
        Assert.Equal(("Brightpath Learning", CustomerState.Active), (customer.Name, customer.State));
        var contacts = await StoredContactsAsync(customer.Id, cancellationToken);
        Assert.Equal(["Ada Moreno", "Ben Okafor"], contacts.Select(contact => contact.FullName));
        Assert.All(contacts, contact => Assert.Equal(CustomerContactState.Active, contact.State));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_reader_of_customers_sees_a_customer_and_its_contacts_read_only()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Dialog Reading Customers", cancellationToken);
        Guid customer;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var customers = keeper.Get<CustomerService>();
            customer = (await customers.CreateCustomerAsync(new Customer { Name = "Lakeside School" }, CustomerWorkflowAttribute.ActivateStep, cancellationToken)).Value;
            Assert.True((await customers.Contacts.CreateCustomerContactAsync(new CustomerContact { CustomerId = customer, FirstName = "Hal", LastName = "Berg" }, CustomerContactWorkflowAttribute.ActivateStep, cancellationToken)).IsSuccess);
        }

        var reader = await scenario.AddPersonAsync(this.database, "Reader", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, cancellationToken, RolePermission.CustomersView);
        await using var circuit = await CircuitAsync(reader);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => CustomerDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<CustomerService>(), customer));
        WaitForDialog(rendered);
        Assert.True(rendered.Find("[name=customer-name]").HasAttribute("readonly"));
        Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition]"));
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=contacts]"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
        Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Add contact");
        ClickOnce(rendered, () => rendered.Find(".state-entity-dialog tr.list-row").QuerySelector("button.list-row__open")!);
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<CustomerContactDialog>()), Wait);
        WaitForDialog(rendered);
        Assert.True(rendered.Find("[name=contact-first-name]").HasAttribute("readonly"));
        Assert.Empty(rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("button[data-transition]"));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_keeper_of_customers_without_the_read_permission_sees_and_adds_contacts()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator) = await ArrangeAsync("Dialog Keeping Customers", cancellationToken);
        Guid customer;
        await using (var setup = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var customers = setup.Get<CustomerService>();
            customer = (await customers.CreateCustomerAsync(new Customer { Name = "Harbor College" }, CustomerWorkflowAttribute.ActivateStep, cancellationToken)).Value;
            Assert.True((await customers.Contacts.CreateCustomerContactAsync(new CustomerContact { CustomerId = customer, FirstName = "Ivy", LastName = "Nash" }, CustomerContactWorkflowAttribute.ActivateStep, cancellationToken)).IsSuccess);
        }

        var keeper = await scenario.AddPersonAsync(this.database, "Keeper", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, keeper, cancellationToken, RolePermission.CustomersManage);
        await using var circuit = await CircuitAsync(keeper);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => CustomerDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<CustomerService>(), customer));
        WaitForDialog(rendered);

        Assert.True(rendered.Find("[name=customer-name]").HasAttribute("readonly"));
        rendered.Edit();
        Assert.False(rendered.Find("[name=customer-name]").HasAttribute("readonly"));
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=contacts]"));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
        Assert.Contains(rendered.FindAll(".state-entity-dialog button"), button => TextOf(button) == "Add contact");
    }

    private async Task<(StaffScenario Scenario, Guid Coordinator)> ArrangeAsync(string name, CancellationToken cancellationToken)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, TwoTenantScenario.UniqueName(name));
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", cancellationToken);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        return (scenario, coordinator);
    }

    private async Task<Customer> StoredCustomerAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Customers.AsNoTracking().SingleAsync(row => row.TenantId == tenantId, cancellationToken);
    }

    private async Task<List<CustomerContact>> StoredContactsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.CustomerContacts.AsNoTracking().Where(row => row.CustomerId == customerId).OrderBy(row => row.LastName).ToListAsync(cancellationToken);
    }

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static IRenderedComponent<IComponent> HostDialogs(PageContext page) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
    });

    private static void Host<TPage>(RenderTreeBuilder builder) where TPage : IComponent
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
        builder.CloseComponent();
        builder.OpenComponent<TPage>(1);
        builder.CloseComponent();
    }

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
    }

    private async Task<Circuit> CircuitAsync(Guid userId)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId));
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime);
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly RuntimeExecutionContext runtime;

        public Circuit(ServiceHost host, RuntimeExecutionContext runtime)
        {
            this.host = host;
            this.runtime = runtime;
        }

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(TimeProvider.System);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<CustomerService>());
            page.Services.AddSingleton(this.host.Get<CustomerContactService>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
