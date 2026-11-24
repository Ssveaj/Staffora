using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Home;
using Staffora.Services.Authorization;
using Staffora.Services.Customers;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class CustomerPortalTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public CustomerPortalTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_staffing_company_enables_Brightpath_Learnings_portal_and_its_customer_administrator_signs_in()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Company A", Ct, customer: "Brightpath Learning", exactCustomerName: true);

        Assert.True(await SignsInAsync(scenario.AdministratorEmail, scenario.AdministratorPassword));

        var session = await SessionCircuit.ResolveAsync(this.database, scenario.AdministratorId);
        Assert.Equal((scenario.AdministratorId, (Guid?)scenario.PortalTenantId, false), (session.UserId, session.TenantId, session.Current.HasPlatformReach()));
        await using var host = ServiceHost.Create(this.database, session);
        var permissions = host.Get<PermissionService>();
        Assert.Equal(RolePresets.CustomerAdmin.Permissions.Order(), (await permissions.GetUserPermissionsAsync(Ct)).Order());
        Assert.Equal(TenantEdition.CustomerPortal, await permissions.GetEditionAsync(Ct));

        Assert.Equal("Brightpath Learning", scenario.Portal.Name.Split(" (")[0]);
        var home = await host.Get<TenantHomeService>().ReadPortalAsync(Ct);
        Assert.True(home.IsSuccess, home.Error);
        Assert.Equal((await TenantNameAsync(scenario.Supplier.TenantId), true, true, true), (home.Value!.Supplier, home.Value.MayFollowRequests, home.Value.MayRequest, home.Value.MayManageUsers));
    }

    [Fact]
    public async Task Enabling_writes_the_managed_tenant_its_supplier_its_portal_roles_and_its_administrator_on_its_behalf_and_links_the_customer()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Northwind Staffing", Ct);
        var portal = scenario.PortalTenantId;
        var account = SystemAccount.IdFor(portal);
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read what enabling a portal wrote");

        var tenant = await system.Tenants.AsNoTracking().SingleAsync(row => row.Id == portal, Ct);
        Assert.Equal((TenantState.Active, (Guid?)scenario.Supplier.TenantId, false, account), (tenant.State, tenant.ParentTenantId, tenant.IsRootTenant, tenant.CreatedById));
        Assert.Equal(TenantEdition.CustomerPortal, TenantEditions.Of(tenant.ParentTenantId));
        var supplierTenant = await system.Tenants.AsNoTracking().SingleAsync(row => row.Id == scenario.Supplier.TenantId, Ct);
        Assert.True(TenantStates.AreLinked(portal, tenant.ParentTenantId, supplierTenant.Id, supplierTenant.ParentTenantId));

        var customer = await system.Customers.AsNoTracking().SingleAsync(row => row.Id == scenario.CustomerId, Ct);
        Assert.Equal((CustomerState.Active, (Guid?)portal, (Guid?)scenario.RootId), (customer.State, customer.LinkedTenantId, customer.DefaultUnitId));
        Assert.Equal(scenario.Supplier.OwnerId, customer.ModifiedById);

        Assert.True(await system.Users.AnyAsync(user => user.Id == account && user.IsSystemAccount && user.TenantId == portal, Ct));
        var root = await system.OrganizationUnits.AsNoTracking().SingleAsync(unit => unit.TenantId == portal, Ct);
        Assert.Equal(OrganizationUnitType.Company, root.UnitType);
        Assert.True(await system.TenantSettings.AnyAsync(setting => setting.TenantId == portal, Ct));

        var supplier = await system.Suppliers.AsNoTracking().SingleAsync(row => row.TenantId == portal, Ct);
        Assert.Equal((supplierTenant.Name, scenario.Supplier.TenantId, SupplierState.Active, account), (supplier.Name, supplier.LinkedTenantId, supplier.State, supplier.CreatedById));

        var roles = await system.TenantRoles.AsNoTracking().Include(role => role.Permissions).Where(role => role.TenantId == portal).ToListAsync(Ct);
        Assert.Equal(
            RolePresets.Portal.Select(preset => (preset.Name, Permissions: string.Join(",", preset.Permissions.Order()))).OrderBy(row => row.Name),
            roles.Select(role => (role.Name, Permissions: string.Join(",", role.Permissions.Select(row => row.Permission).Order()))).OrderBy(row => row.Name));
        Assert.DoesNotContain(roles, role => role.IsSystemOwner || role.ApplyResourceMembershipToUser || role.State != TenantRoleState.Active);
        var administrator = await system.Users.AsNoTracking().SingleAsync(user => user.Id == scenario.AdministratorId, Ct);
        Assert.Equal((portal, UserState.Active, false, scenario.AdministratorEmail, account), (administrator.TenantId, administrator.State, administrator.IsSystemAccount, administrator.UserName, administrator.CreatedById));
        var holding = await system.OrganizationUnitUserRoles.AsNoTracking().SingleAsync(row => row.UserId == scenario.AdministratorId, Ct);
        Assert.Equal((root.Id, roles.Single(role => role.Name == RolePresets.CustomerAdmin.Name).Id), (holding.OrganizationUnitId, holding.TenantRoleId));

        var stamps = await system.Users.Where(user => user.TenantId == portal && !user.IsSystemAccount).Select(user => new { user.CreatedById, user.ImpersonatedById })
            .Concat(system.TenantRoles.Where(role => role.TenantId == portal).Select(role => new { role.CreatedById, role.ImpersonatedById }))
            .Concat(system.TenantRolePermissions.Where(row => row.TenantId == portal).Select(row => new { row.CreatedById, row.ImpersonatedById }))
            .Concat(system.OrganizationUnitUserRoles.Where(row => row.TenantId == portal).Select(row => new { row.CreatedById, row.ImpersonatedById }))
            .Concat(system.OrganizationUnits.Where(row => row.TenantId == portal).Select(row => new { row.CreatedById, row.ImpersonatedById }))
            .Concat(system.Suppliers.Where(row => row.TenantId == portal).Select(row => new { row.CreatedById, row.ImpersonatedById }))
            .Concat(system.StateHistoryEntries.Where(row => row.TenantId == portal).Select(row => new { row.CreatedById, row.ImpersonatedById }))
            .ToListAsync(Ct);
        Assert.NotEmpty(stamps);
        Assert.All(stamps, stamp => Assert.Equal((account, (Guid?)null), (stamp.CreatedById, stamp.ImpersonatedById)));

        var history = await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.TenantId == portal).ToListAsync(Ct);
        Assert.Contains(history, entry => entry.EntityId == portal && entry.TransitionKey == TenantWorkflowAttribute.ActivateStep && entry.ToState == (int)TenantState.Active);
        Assert.Contains(history, entry => entry.EntityId == supplier.Id && entry.TransitionKey == SupplierWorkflowAttribute.OpenStep && entry.ToState == (int)SupplierState.Active);
        Assert.Contains(history, entry => entry.EntityId == administrator.Id && entry.TransitionKey == ApplicationUserWorkflowAttribute.CreateStep);
        Assert.Equal(roles.Count, history.Count(entry => entry.TransitionKey == TenantRoleWorkflowAttribute.CreateStep));
        Assert.All(history, entry => Assert.Equal((account, account), (entry.ActorId, entry.CreatedById)));
        Assert.DoesNotContain(history, entry => entry.ActorId == scenario.Supplier.OwnerId);

        Assert.Contains(await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == scenario.CustomerId).Select(entry => entry.TransitionKey).ToListAsync(Ct), key => key == CustomerWorkflowAttribute.EnablePortalStep);
    }

    [Fact]
    public async Task The_initial_password_is_answered_once_and_is_never_stored_in_clear_or_logged()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Password Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Clearwater Academy"), Ct);
        var email = $"admin.{Guid.NewGuid():N}@clearwater.example";
        var logged = new ConcurrentQueue<string>();
        var enabled = await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(rootId, "Ali", "Stone", email), Ct,
            services => services.AddLogging(logging => logging.AddProvider(new EveryLine(logged)).SetMinimumLevel(LogLevel.Trace)));
        Assert.True(enabled.IsSuccess, enabled.Error);
        var password = enabled.Value!.InitialPassword;

        Assert.Equal(16, password.Length);
        Assert.Contains(password, enabled.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(password, enabled.Value.ToString(), StringComparison.Ordinal);

        await using (var host = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var users = host.Get<UserManager<ApplicationUser>>();
            var administrator = (await users.FindByEmailAsync(email))!;
            Assert.NotEqual(password, administrator.PasswordHash);
            Assert.True(await users.CheckPasswordAsync(administrator, password));
        }

        Assert.Empty(await ColumnsHoldingAsync(password));
        Assert.NotEmpty(logged);
        Assert.DoesNotContain(logged, line => line.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_a_holder_of_CustomerPortalEnable_in_the_staffing_company_enables_a_portal_and_a_refusal_writes_nothing()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Gate Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Gate Customer"), Ct);
        var coordinator = await TwoTenantScenario.AddUserAsync(this.database, supplier.Tenant, "Casey", Ct);
        await TwoTenantScenario.GrantAsync(this.database, supplier.Tenant, coordinator, Ct, [.. RolePresets.Coordinator.Permissions]);
        var draft = await AddDraftCustomerAsync(supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Draft Customer"));
        var otherRoot = await TwoTenantScenario.RootUnitIdAsync(this.database, (await TwoTenantScenario.CreateAsync(this.database, Ct)).A, Ct);
        var before = await CountsAsync();
        var request = new CustomerPortalRequest(rootId, "Sam", "Lee", $"sam.{Guid.NewGuid():N}@gate.example");

        Assert.Equal(CustomerService.NoPortalEnable, (await PortalScenario.EnableAsync(this.database, supplier.RuntimeOf(coordinator), customerId, request, Ct)).Error);
        await using (var host = ServiceHost.Create(this.database, supplier.RuntimeOf(coordinator)))
        {
            var refused = await host.Get<WorkflowService>().TransitionEntityAsync<CustomerState, Customer>(customerId, CustomerWorkflowAttribute.EnablePortalStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.False(refused.IsSuccess);
            var offered = await host.Get<WorkflowService>().GetAvailableTransitionsAsync<CustomerState, Customer>(customerId, CustomerWorkflowQueries.WithWorkflowGraph, Ct);
            Assert.False(offered.Value!.Single(step => step.Transition.Key == CustomerWorkflowAttribute.EnablePortalStep).MayRun);
        }

        Assert.False((await PortalScenario.EnableAsync(this.database, supplier.Platform.PlatformOwnerRuntime(), customerId, request, Ct)).IsSuccess);

        await using (var host = ServiceHost.Create(this.database, supplier.OwnerRuntime()))
        {
            Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<CustomerState, Customer>(customerId, CustomerWorkflowAttribute.EnablePortalStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        }

        Assert.False((await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), draft, request, Ct)).IsSuccess);
        Assert.Equal(CustomerService.PortalUnitRequired, (await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, request with { UnitId = otherRoot }, Ct)).Error);
        Assert.Equal(CustomerService.PortalAdministratorRequired, (await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, request with { Email = " " }, Ct)).Error);

        Assert.Equal(before, await CountsAsync());
        await AssertNoPortalAsync(customerId);
        await AssertNoPortalAsync(draft);
    }

    [Fact]
    public async Task A_second_enabling_and_two_clicks_at_once_create_one_portal_and_an_address_in_use_is_refused_with_nothing_written()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Twice Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);

        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Twice Customer"), Ct);
        var first = $"first.{Guid.NewGuid():N}@twice.example";
        var second = $"second.{Guid.NewGuid():N}@twice.example";
        var both = await Task.WhenAll(
            PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(rootId, "Ann", "One", first), Ct),
            PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(rootId, "Bob", "Two", second), Ct));
        Assert.Single(both, result => result.IsSuccess);
        Assert.Equal(1, await ManagedTenantsOfAsync(supplier.TenantId));
        Assert.Equal(1, await UsersWithAsync(first) + await UsersWithAsync(second));

        var other = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Other Customer"), Ct);
        var before = await CountsAsync();
        var again = await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(rootId, "Cy", "Three", $"third.{Guid.NewGuid():N}@twice.example"), Ct);
        Assert.Equal(CustomerWorkflowAttribute.PortalNotEnabledReason, again.Error);
        Assert.Equal(before, await CountsAsync());

        var winner = both.Single(result => result.IsSuccess).Value!.AdministratorEmail;
        var late = await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(rootId, "Ann", "One", winner), Ct);
        Assert.Equal(CustomerWorkflowAttribute.PortalNotEnabledReason, late.Error);
        Assert.Equal(before, await CountsAsync());

        var held = await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), other, new CustomerPortalRequest(rootId, "Dee", "Four", both.Single(result => result.IsSuccess).Value!.AdministratorEmail), Ct);
        Assert.Equal(UserService.SignInNameUnavailable, held.Error);
        Assert.Equal(before, await CountsAsync());
        await AssertNoPortalAsync(other);
    }

    [Fact]
    public async Task A_failure_anywhere_in_the_step_leaves_neither_the_customers_link_nor_any_row_of_the_portal()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Rollback Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Rollback Customer"), Ct);
        var before = await CountsAsync();

        var failing = new FailsTheSuppliersSave();
        await using (var host = ServiceHost.Create(this.database, supplier.OwnerRuntime(), interceptors: [failing]))
        {
            var customers = host.Get<CustomerService>();
            var updater = await customers.LoadCustomerUpdaterAsync(customerId, Ct);
            var enabled = await customers.EnableCustomerPortalAsync(updater.Value!, new CustomerPortalRequest(rootId, "Eve", "Five", $"eve.{Guid.NewGuid():N}@rollback.example"), Ct);
            Assert.False(enabled.IsSuccess);
        }

        Assert.True(failing.Failed);
        Assert.Equal(before, await CountsAsync());
        await AssertNoPortalAsync(customerId);
        Assert.Equal(0, await ManagedTenantsOfAsync(supplier.TenantId));
    }

    [Fact]
    public async Task The_staffing_company_and_its_customers_portal_read_none_of_each_others_rows()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Isolated Staffing", Ct);

        await using (var context = this.database.CreateContext(scenario.Supplier.OwnerRuntime()))
        {
            Assert.Equal([scenario.Supplier.TenantId], await context.Tenants.Select(tenant => tenant.Id).ToListAsync(Ct));
            Assert.DoesNotContain(await context.Users.Select(user => user.Id).ToListAsync(Ct), id => id == scenario.AdministratorId);
            Assert.Empty(await context.Suppliers.ToListAsync(Ct));
            Assert.DoesNotContain(await context.TenantRoles.Select(role => role.TenantId).ToListAsync(Ct), tenant => tenant == scenario.PortalTenantId);
        }

        await using (var context = this.database.CreateContext(scenario.AdministratorRuntime()))
        {
            Assert.Equal([scenario.PortalTenantId], await context.Tenants.Select(tenant => tenant.Id).ToListAsync(Ct));
            Assert.Empty(await context.Customers.ToListAsync(Ct));
            Assert.All(await context.Users.Select(user => user.TenantId).ToListAsync(Ct), tenant => Assert.Equal(scenario.PortalTenantId, tenant));
            Assert.All(await context.OrganizationUnits.Select(unit => unit.TenantId).ToListAsync(Ct), tenant => Assert.Equal(scenario.PortalTenantId, tenant));
            Assert.Single(await context.Suppliers.ToListAsync(Ct));
        }

        await using var portalHost = ServiceHost.Create(this.database, scenario.AdministratorRuntime());
        Assert.Equal(CustomerService.NoView, (await portalHost.Get<CustomerService>().LoadCustomerUpdaterAsync(scenario.CustomerId, Ct)).Error);
    }

    [Fact]
    public async Task A_customer_deactivated_afterwards_keeps_its_portal_its_delete_is_refused_and_a_tenant_name_in_use_is_taken_with_a_number()
    {
        var customerName = TwoTenantScenario.UniqueName("Harbour School");
        await using (var system = this.database.CreateSystemContext())
        {
            system.Tenants.Add(new Tenant { Name = customerName, State = TenantState.Active });
            await system.SaveChangesAsync(Ct);
        }

        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Harbour Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), customerName, Ct);
        var email = $"admin.{Guid.NewGuid():N}@harbour.example";
        var enabled = await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(rootId, "Fay", "Six", email), Ct);
        Assert.True(enabled.IsSuccess, enabled.Error);
        Assert.DoesNotContain("(2)", enabled.Message!, StringComparison.Ordinal);
        Guid portal;
        await using (var system = this.database.CreateSystemContext())
        {
            portal = (await system.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, Ct)).LinkedTenantId!.Value;
            Assert.Equal($"{customerName} (2)", await system.Tenants.Where(tenant => tenant.Id == portal).Select(tenant => tenant.Name).SingleAsync(Ct));
        }

        await using (var host = ServiceHost.Create(this.database, supplier.OwnerRuntime()))
        {
            var workflows = host.Get<WorkflowService>();
            Assert.True((await workflows.TransitionEntityAsync<CustomerState, Customer>(customerId, CustomerWorkflowAttribute.DeactivateStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
            var delete = await workflows.TransitionEntityAsync<CustomerState, Customer>(customerId, CustomerWorkflowAttribute.DeleteStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.Equal(CustomerWorkflowAttribute.KeepsItsPortalReason, delete.Error);
            Assert.True(await SignsInAsync(email, enabled.Value!.InitialPassword));
            Assert.True((await workflows.TransitionEntityAsync<CustomerState, Customer>(customerId, CustomerWorkflowAttribute.ReactivateStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var stored = await system.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, Ct);
            Assert.Equal((CustomerState.Active, (Guid?)portal), (stored.State, stored.LinkedTenantId));
        }

        var options = await OptionsAsync(supplier.OwnerRuntime(), customerId);
        Assert.Equal((true, true, supplier.Tenant.Name), (options.Shown, options.Enabled, options.UnitName));
        Assert.Empty(options.Units);
    }

    [Fact]
    public async Task The_customer_dialogs_options_say_where_the_portal_stands_by_this_tenants_unit_only_and_offer_its_units_to_a_holder_of_CustomerPortalEnable()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Options Staffing", activate: true);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Options Customer"), Ct);
        var coordinator = await TwoTenantScenario.AddUserAsync(this.database, supplier.Tenant, "Casey", Ct);
        await TwoTenantScenario.GrantAsync(this.database, supplier.Tenant, coordinator, Ct, [.. RolePresets.Coordinator.Permissions]);

        var owner = await OptionsAsync(supplier.OwnerRuntime(), customerId);
        Assert.Equal((true, false), (owner.Shown, owner.Enabled));
        Assert.Equal([supplier.Tenant.Name], owner.Units.Select(unit => unit.Name));
        Assert.Empty((await OptionsAsync(supplier.RuntimeOf(coordinator), customerId)).Units);
    }

    [Fact]
    public async Task An_enabling_still_running_in_the_same_circuit_refuses_a_second_one_for_the_customer_and_keeps_what_the_first_prepared()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Inflight Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Inflight Customer"), Ct);
        var before = await CountsAsync();
        await using var host = ServiceHost.Create(this.database, supplier.OwnerRuntime());
        var provisioner = host.Get<CustomerPortalProvisioner>();
        var firstEmail = $"first.{Guid.NewGuid():N}@inflight.example";
        var first = new ApplicationUser { FirstName = "Ann", LastName = "One", Email = firstEmail, UserName = firstEmail };
        Assert.True(provisioner.Prepare(customerId, rootId, first));

        var customers = host.Get<CustomerService>();
        var updater = await customers.LoadCustomerUpdaterAsync(customerId, Ct);
        var second = await customers.EnableCustomerPortalAsync(updater.Value!, new CustomerPortalRequest(rootId, "Bob", "Two", $"second.{Guid.NewGuid():N}@inflight.example"), Ct);
        Assert.Equal(CustomerService.PortalBeingEnabled, second.Error);
        Assert.Equal(before, await CountsAsync());
        await AssertNoPortalAsync(customerId);

        provisioner.LinkPortal(new Customer { Id = customerId, TenantId = supplier.Tenant.TenantId, Name = "Inflight" });
        Assert.Same(first, provisioner.TakeDue(customerId)!.Administrator);
    }

    [Fact]
    public async Task A_platform_owner_impersonating_the_system_owner_enables_a_portal_whose_every_row_names_its_system_account_and_no_impersonator()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Impersonated Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Impersonated Customer"), Ct);
        var runtime = supplier.Platform.PlatformOwnerRuntime();
        runtime.StartImpersonation(supplier.Tenant.OwnerId, supplier.Tenant.TenantId);
        var enabled = await PortalScenario.EnableAsync(this.database, runtime, customerId, new CustomerPortalRequest(rootId, "Ida", "Seven", $"ida.{Guid.NewGuid():N}@impersonated.example"), Ct);
        Assert.True(enabled.IsSuccess, enabled.Error);

        await using var system = this.database.CreateSystemContext();
        var portal = (await system.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, Ct)).LinkedTenantId!.Value;
        var account = SystemAccount.IdFor(portal);

        var tables = system.Model.GetEntityTypes()
            .Where(type => typeof(ITenantEntity).IsAssignableFrom(type.ClrType) && type.GetTableName() is not null && type.BaseType is null)
            .Select(type => (Schema: type.GetSchema() ?? "dbo", Table: type.GetTableName()!))
            .Distinct()
            .ToList();
        Assert.NotEmpty(tables);
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(Ct);
        var rows = 0;
        foreach (var (schema, table) in tables)
        {
            await using var count = new SqlCommand($"SELECT COUNT(*), SUM(CASE WHEN CreatedById <> @account OR ImpersonatedById IS NOT NULL THEN 1 ELSE 0 END) FROM [{schema}].[{table}] WHERE TenantId = @portal", connection);
            count.Parameters.AddWithValue("@account", account);
            count.Parameters.AddWithValue("@portal", portal);
            await using var reader = await count.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            rows += reader.GetInt32(0);
            Assert.True(reader.IsDBNull(1) || reader.GetInt32(1) == 0, $"{table} has a row of the portal stamped with someone else or an impersonator");
        }

        Assert.True(rows > 0);

        foreach (var (schema, table) in tables)
        {
            var keys = new List<string>();
            await using (var columns = new SqlCommand("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND DATA_TYPE = 'uniqueidentifier'", connection))
            {
                columns.Parameters.AddWithValue("@schema", schema);
                columns.Parameters.AddWithValue("@table", table);
                await using var reader = await columns.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    keys.Add(reader.GetString(0));
                }
            }

            foreach (var key in keys)
            {
                await using var naming = new SqlCommand($"SELECT COUNT(*) FROM [{schema}].[{table}] WHERE TenantId = @portal AND [{key}] IN (@owner, @platformOwner)", connection);
                naming.Parameters.AddWithValue("@portal", portal);
                naming.Parameters.AddWithValue("@owner", supplier.Tenant.OwnerId);
                naming.Parameters.AddWithValue("@platformOwner", supplier.Platform.OwnerId);
                Assert.True((int)(await naming.ExecuteScalarAsync(Ct))! == 0, $"{table}.{key} names a user of another tenant");
            }
        }

        var step = await system.StateHistoryEntries.AsNoTracking().SingleAsync(entry => entry.EntityId == customerId && entry.TransitionKey == CustomerWorkflowAttribute.EnablePortalStep, Ct);
        Assert.Equal((supplier.Tenant.OwnerId, (Guid?)supplier.Platform.OwnerId), (step.ActorId, step.ImpersonatedById));
    }

    [Fact]
    public async Task A_staffing_company_that_is_not_active_and_a_customer_that_is_not_active_enable_no_portal_and_write_nothing()
    {
        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Held Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var request = new CustomerPortalRequest(rootId, "Hal", "Eight", $"hal.{Guid.NewGuid():N}@held.example");

        var inactive = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Inactive Customer"), Ct);
        await using (var host = ServiceHost.Create(this.database, supplier.OwnerRuntime()))
        {
            Assert.True((await host.Get<WorkflowService>().TransitionEntityAsync<CustomerState, Customer>(inactive, CustomerWorkflowAttribute.DeactivateStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).IsSuccess);
        }

        var before = await CountsAsync();
        Assert.False((await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), inactive, request, Ct)).IsSuccess);
        Assert.Equal(before, await CountsAsync());
        await AssertNoPortalAsync(inactive);

        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), TwoTenantScenario.UniqueName("Held Customer"), Ct);
        await using (var host = ServiceHost.Create(this.database, supplier.OwnerRuntime()))
        {
            var customers = host.Get<CustomerService>();
            var updater = await customers.LoadCustomerUpdaterAsync(customerId, Ct);
            await SuspendAsync(supplier, supplier.Tenant.TenantId);
            before = await CountsAsync();
            Assert.False((await customers.EnableCustomerPortalAsync(updater.Value!, request, Ct)).IsSuccess);
        }

        Assert.Equal(before, await CountsAsync());
        await AssertNoPortalAsync(customerId);
        Assert.Equal(0, await ManagedTenantsOfAsync(supplier.Tenant.TenantId));

        var setup = await OnboardedScenario.CreateAsync(this.database, Ct, "Setup Staffing", activate: false);
        var setupRoot = await TwoTenantScenario.RootUnitIdAsync(this.database, setup.Tenant, Ct);
        await using (var host = ServiceHost.Create(this.database, setup.OwnerRuntime()))
        {
            var created = await host.Get<CustomerService>().CreateCustomerAsync(new Customer { Name = TwoTenantScenario.UniqueName("Setup Customer") }, CustomerWorkflowAttribute.ActivateStep, Ct);
            Assert.True(created.IsSuccess, created.Error);
            before = await CountsAsync();
            var enabled = await PortalScenario.EnableAsync(this.database, setup.OwnerRuntime(), created.Value, request with { UnitId = setupRoot }, Ct);
            Assert.Equal(CustomerService.PortalRequiresActiveCompany, enabled.Error);
            Assert.Equal(before, await CountsAsync());
            await AssertNoPortalAsync(created.Value);
        }
    }

    [Fact]
    public async Task The_portals_system_account_never_signs_in_and_a_suspended_portal_signs_nobody_in_until_it_is_reactivated()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Suspended Portal Staffing", Ct);
        await using (var host = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var account = (await host.Get<UserManager<ApplicationUser>>().FindByIdAsync(SystemAccount.IdFor(scenario.PortalTenantId).ToString()))!;
            Assert.True(account.IsSystemAccount);
            Assert.False(await host.Get<SignInManager<ApplicationUser>>().CanSignInAsync(account));
        }

        Assert.True(await SignsInAsync(scenario.AdministratorEmail, scenario.AdministratorPassword));
        await SuspendAsync(scenario.Supplier, scenario.PortalTenantId);
        Assert.False(await SignsInAsync(scenario.AdministratorEmail, scenario.AdministratorPassword));

        await using (var host = ServiceHost.Create(this.database, scenario.Supplier.Platform.PlatformOwnerRuntime()))
        {
            var reactivated = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(scenario.PortalTenantId, "tenant-reactivate", TenantWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
            Assert.True(reactivated.IsSuccess, reactivated.Error);
        }

        Assert.True(await SignsInAsync(scenario.AdministratorEmail, scenario.AdministratorPassword));
    }

    [Fact]
    public async Task A_portal_whose_customers_name_another_tenant_holds_keeps_the_customers_own_name_on_everything_its_people_read()
    {
        var customerName = TwoTenantScenario.UniqueName("Lakeside College");
        await using (var system = this.database.CreateSystemContext())
        {
            system.Tenants.Add(new Tenant { Name = customerName, State = TenantState.Active });
            await system.SaveChangesAsync(Ct);
        }

        var supplier = await OnboardedScenario.CreateAsync(this.database, Ct, "Lakeside Staffing", activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, supplier.Tenant, Ct);
        var customerId = await PortalScenario.AddActiveCustomerAsync(this.database, supplier.OwnerRuntime(), customerName, Ct);
        var enabled = await PortalScenario.EnableAsync(this.database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(rootId, "Liv", "Nine", $"liv.{Guid.NewGuid():N}@lakeside.example"), Ct);
        Assert.True(enabled.IsSuccess, enabled.Error);

        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: read the portal's own names");
            var portal = (await system.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, Ct)).LinkedTenantId!.Value;
            Assert.Equal($"{customerName} (2)", await system.Tenants.Where(tenant => tenant.Id == portal).Select(tenant => tenant.Name).SingleAsync(Ct));
            Assert.Equal([customerName], await system.OrganizationUnits.Where(unit => unit.TenantId == portal).Select(unit => unit.Name).ToListAsync(Ct));
        }
    }

    [Fact]
    public async Task The_portal_links_of_a_customer_are_written_by_its_step_alone_and_never_by_a_save()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Pinned Staffing", Ct);
        var other = await TwoTenantScenario.AddTenantAsync(this.database, TwoTenantScenario.UniqueName("Unrelated Company"), Ct);
        await using (var host = ServiceHost.Create(this.database, scenario.Supplier.OwnerRuntime()))
        {
            var customers = host.Get<CustomerService>();
            var updater = (await customers.LoadCustomerUpdaterAsync(scenario.CustomerId, Ct)).Value!;
            updater.Entity.LinkedTenantId = other.TenantId;
            Assert.False((await customers.UpdateCustomerAndTransitionAsync(updater, CustomerWorkflowAttribute.SaveStep, null, Ct)).IsSuccess);

            updater = (await customers.LoadCustomerUpdaterAsync(scenario.CustomerId, Ct)).Value!;
            updater.Entity.DefaultUnitId = Guid.NewGuid();
            Assert.False((await customers.UpdateCustomerAndTransitionAsync(updater, CustomerWorkflowAttribute.SaveStep, null, Ct)).IsSuccess);
        }

        await using var system = this.database.CreateSystemContext();
        var stored = await system.Customers.AsNoTracking().SingleAsync(row => row.Id == scenario.CustomerId, Ct);
        Assert.Equal(((Guid?)scenario.PortalTenantId, (Guid?)scenario.RootId), (stored.LinkedTenantId, stored.DefaultUnitId));
    }

    private async Task SuspendAsync(OnboardedScenario scenario, Guid tenantId)
    {
        await using var host = ServiceHost.Create(this.database, scenario.Platform.PlatformOwnerRuntime());
        var suspended = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, "tenant-suspend", TenantWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct);
        Assert.True(suspended.IsSuccess, suspended.Error);
    }

    private async Task<CustomerPortalOptions> OptionsAsync(IRuntimeExecutionContext runtime, Guid customerId)
    {
        await using var host = ServiceHost.Create(this.database, runtime);
        var options = await host.Get<CustomerService>().GetCustomerEditorOptionsAsync(customerId, Ct);
        Assert.True(options.IsSuccess, options.Error);
        return options.Value!.Portal;
    }

    private async Task<Guid> AddDraftCustomerAsync(IRuntimeExecutionContext runtime, string name)
    {
        await using var host = ServiceHost.Create(this.database, runtime);
        var created = await host.Get<CustomerService>().CreateCustomerAsync(new Customer { Name = name }, CustomerWorkflowAttribute.SaveDraftStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<bool> SignsInAsync(string email, string password)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = await host.Get<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        Assert.NotNull(user);
        var result = await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(user, password, lockoutOnFailure: false);
        return result.Succeeded;
    }

    private async Task<string> TenantNameAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Tenants.Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.Name).SingleAsync(Ct);
    }

    private async Task AssertNoPortalAsync(Guid customerId)
    {
        await using var system = this.database.CreateSystemContext();
        var customer = await system.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, Ct);
        Assert.Equal(((Guid?)null, (Guid?)null), (customer.LinkedTenantId, customer.DefaultUnitId));
    }

    private async Task<int> ManagedTenantsOfAsync(Guid staffingCompany)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Tenants.CountAsync(tenant => tenant.ParentTenantId == staffingCompany, Ct);
    }

    private async Task<int> UsersWithAsync(string email)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.CountAsync(user => user.Email == email, Ct);
    }

    private async Task<(int Tenants, int Users, int Units, int Settings, int Roles, int Holdings, int Suppliers, int History)> CountsAsync()
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: count what enabling a portal writes");
        return (await system.Tenants.CountAsync(Ct), await system.Users.CountAsync(Ct), await system.OrganizationUnits.CountAsync(Ct), await system.TenantSettings.CountAsync(Ct),
            await system.TenantRoles.CountAsync(Ct), await system.OrganizationUnitUserRoles.CountAsync(Ct), await system.Suppliers.CountAsync(Ct), await system.StateHistoryEntries.CountAsync(Ct));
    }

    private async Task<List<string>> ColumnsHoldingAsync(string text)
    {
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(Ct);
        var columns = new List<(string Table, string Column)>();
        await using (var list = new SqlCommand("SELECT TABLE_SCHEMA + '.' + TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE IN ('nvarchar', 'varchar', 'nchar', 'char', 'ntext', 'text')", connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var holding = new List<string>();
        foreach (var (table, column) in columns)
        {
            var schemaAndName = table.Split('.');
            await using var probe = new SqlCommand($"SELECT COUNT(*) FROM [{schemaAndName[0]}].[{schemaAndName[1]}] WHERE CHARINDEX(@text, CAST([{column}] AS nvarchar(max))) > 0", connection);
            probe.Parameters.AddWithValue("@text", text);
            if ((int)(await probe.ExecuteScalarAsync(Ct))! > 0)
            {
                holding.Add($"{table}.{column}");
            }
        }

        return holding;
    }

    private sealed class EveryLine : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> lines;

        public EveryLine(ConcurrentQueue<string> lines)
        {
            this.lines = lines;
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            this.lines.Enqueue($"{formatter(state, exception)} {state} {exception}");

        public void Dispose()
        {
        }
    }

    private sealed class FailsTheSuppliersSave : SaveChangesInterceptor
    {
        public bool Failed { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && context.ChangeTracker.Entries<Supplier>().Any(entry => entry.State == EntityState.Added))
            {
                Failed = true;
                throw new InvalidOperationException("The supplier party's save fails.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
