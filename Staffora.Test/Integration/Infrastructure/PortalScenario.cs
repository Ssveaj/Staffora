using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Customers;

namespace Staffora.Test.Integration.Infrastructure;

public sealed record PortalScenario(OnboardedScenario Supplier, Guid CustomerId, string CustomerName, Guid RootId, ScenarioTenant Portal, string AdministratorEmail, string AdministratorPassword)
{
    public Guid PortalTenantId => Portal.TenantId;

    public Guid AdministratorId => Portal.OwnerId;

    public static async Task<PortalScenario> CreateAsync(RealDatabase database, string name, CancellationToken cancellationToken, string customer = "Brightpath Learning", bool exactCustomerName = false, string? portalUnit = null)
    {
        var supplier = await OnboardedScenario.CreateAsync(database, cancellationToken, name, activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(database, supplier.Tenant, cancellationToken);
        var unitId = rootId;
        if (portalUnit is not null)
        {
            await using var host = ServiceHost.Create(database, supplier.OwnerRuntime());
            unitId = await host.Get<Staffora.Services.Services.OrganizationService>().CreateUnitIdAsync(portalUnit, Staffora.Core.Entities.Organization.OrganizationUnitType.Branch, rootId, cancellationToken);
        }

        var customerName = exactCustomerName ? customer : TwoTenantScenario.UniqueName(customer);
        var customerId = await AddActiveCustomerAsync(database, supplier.OwnerRuntime(), customerName, cancellationToken);
        return await ForCustomerAsync(database, supplier, customerId, customerName, rootId, unitId, cancellationToken);
    }

    public static async Task<PortalScenario> ForCustomerAsync(RealDatabase database, OnboardedScenario supplier, Guid customerId, string customerName, Guid rootId, Guid unitId, CancellationToken cancellationToken)
    {
        var email = $"admin.{Guid.NewGuid():N}@{customerName.Replace(' ', '-').ToLowerInvariant()}.example";
        var enabled = await EnableAsync(database, supplier.OwnerRuntime(), customerId, new CustomerPortalRequest(unitId, "Morgan", "Reed", email), cancellationToken);
        Assert.True(enabled.IsSuccess, enabled.Error);

        await using var context = database.CreateSystemContext();
        var portalTenant = (await context.Customers.AsNoTracking().SingleAsync(row => row.Id == customerId, cancellationToken)).LinkedTenantId!.Value;
        var administrator = await context.Users.AsNoTracking().Where(user => user.TenantId == portalTenant && user.Email == email).Select(user => user.Id).SingleAsync(cancellationToken);
        var portalName = await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == portalTenant).Select(tenant => tenant.Name).SingleAsync(cancellationToken);
        return new PortalScenario(supplier, customerId, customerName, rootId, new ScenarioTenant(portalTenant, portalName, administrator), email, enabled.Value!.InitialPassword);
    }

    public static async Task<Guid> AddActiveCustomerAsync(RealDatabase database, IRuntimeExecutionContext runtime, string name, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(database, runtime);
        var created = await host.Get<CustomerService>().CreateCustomerAsync(new Customer { Name = name }, CustomerWorkflowAttribute.ActivateStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    public static async Task<Result<EnabledCustomerPortal>> EnableAsync(RealDatabase database, IRuntimeExecutionContext runtime, Guid customerId, CustomerPortalRequest request, CancellationToken cancellationToken, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null)
    {
        await using var host = ServiceHost.Create(database, runtime, configure);
        var customers = host.Get<CustomerService>();
        var updater = await customers.LoadCustomerUpdaterAsync(customerId, cancellationToken);
        return updater.IsSuccess
            ? await customers.EnableCustomerPortalAsync(updater.Value!, request, cancellationToken)
            : Result<EnabledCustomerPortal>.Failure(updater.Error);
    }

    public IRuntimeExecutionContext AdministratorRuntime() => Portal.Runtime();

    public async Task<Guid> AddOrdererAsync(RealDatabase database, string firstName, CancellationToken cancellationToken)
    {
        var email = $"{firstName.ToLowerInvariant()}.{Guid.NewGuid():N}@portal.example";
        await using var host = ServiceHost.Create(database, AdministratorRuntime());
        var created = await host.Get<Staffora.Services.Services.UserService>().CreateUserAsync(
            new Staffora.Core.Entities.Identity.ApplicationUser { FirstName = firstName, LastName = Portal.Name, Email = email, UserName = email },
            PortalTenantId,
            OrdererPassword,
            ApplicationUserWorkflowAttribute.PortalCreateOrdererStep);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value!.Id;
    }

    public const string OrdererPassword = "Orderer-Test-Pass-1!";

    public IRuntimeExecutionContext RuntimeOf(Guid userId) => Portal.RuntimeOf(userId);
}
