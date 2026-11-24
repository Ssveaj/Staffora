using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Services.Workflow;

namespace Staffora.Services.Customers;

public sealed class CustomerPortalProvisioner : ICustomerPortalProvisioner
{
    private readonly Dictionary<Guid, PreparedPortal> prepared = [];
    private readonly Dictionary<Guid, ManagedTenantOnboarding> due = [];
    private readonly Dictionary<Guid, Guid> linked = [];

    internal bool Prepare(Guid customerId, Guid unitId, ApplicationUser administrator)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        return this.prepared.TryAdd(customerId, new PreparedPortal(unitId, administrator));
    }

    public void LinkPortal(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        if (!this.prepared.TryGetValue(customer.Id, out var portal))
        {
            throw new InvalidOperationException("The customer's portal was not prepared: it is enabled only through the customer service.");
        }

        var tenantId = Guid.NewGuid();
        customer.LinkedTenantId = tenantId;
        customer.DefaultUnitId = portal.UnitId;
        this.due[customer.Id] = new ManagedTenantOnboarding(tenantId, customer.Name, customer.TenantId, portal.Administrator);
    }

    internal ManagedTenantOnboarding? TakeDue(Guid customerId) => this.due.Remove(customerId, out var due) ? due : null;

    internal Guid? LinkedTenantOf(Guid customerId) => this.linked.TryGetValue(customerId, out var tenant) ? tenant : null;

    internal void Forget(Guid customerId)
    {
        this.prepared.Remove(customerId);
        this.due.Remove(customerId);
        this.linked.Remove(customerId);
    }

    internal void Created(Guid customerId, Guid tenantId) => this.linked[customerId] = tenantId;

    private sealed record PreparedPortal(Guid UnitId, ApplicationUser Administrator);
}

public sealed class CustomerPortalFollowUp : IStepFollowUp
{
    private readonly CustomerPortalProvisioner provisioner;
    private readonly TenantOnboardingService onboarding;

    public CustomerPortalFollowUp(CustomerPortalProvisioner provisioner, TenantOnboardingService onboarding)
    {
        this.provisioner = provisioner;
        this.onboarding = onboarding;
    }

    public Type EntityType => typeof(Customer);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (this.provisioner.TakeDue(entity.Id) is not { } portal)
        {
            return;
        }

        await this.onboarding.OnboardManagedTenantAsync(context, portal, cancellationToken);
        this.provisioner.Created(entity.Id, portal.TenantId);

        await Staffora.Services.Catalogue.ServiceOfferQueue.QueueCatalogueAsync(context, portal.TenantId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}
