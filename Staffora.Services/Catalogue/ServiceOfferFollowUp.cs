using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Integration;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Catalogue;

public sealed class ServiceOfferFollowUp : IStepFollowUp
{
    private readonly ServiceGuard guard;

    public ServiceOfferFollowUp(ServiceGuard guard)
    {
        this.guard = guard;
    }

    public Type EntityType => typeof(Service);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (entity is not Service service)
        {
            return;
        }

        var before = this.guard.StoredOfferOf(service.Id);
        var wasOffered = before is not null && ServiceOfferSync.IsOffered(before.State, before.OfferedInPortal);
        var isOffered = ServiceOfferSync.IsOffered(service.State, service.OfferedInPortal);
        var changed = before is null
            || before.Name != service.Name
            || before.Description != service.Description
            || before.DefaultSlotCount != service.DefaultSlotCount;
        if (ServiceOfferSync.KindFor(wasOffered, isOffered, changed) is not { } kind)
        {
            return;
        }

        var payload = kind == ServiceOfferSync.WithdrawnKind
            ? ServiceOfferSync.WriteWithdrawal(service.CrossTenantId)
            : ServiceOfferSync.WriteOffer(new ServiceOffer(service.CrossTenantId, service.Name, service.Description, service.DefaultSlotCount));
        if (await ServiceOfferQueue.PortalsAsync(context, cancellationToken) is { Count: > 0 } portals)
        {
            foreach (var portal in portals)
            {
                IntegrationOutbox.Enqueue(context, kind, portal, ServiceOfferSync.CorrelationFor(service.CrossTenantId, portal), payload);
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }
}

public static class ServiceOfferQueue
{
    internal static async Task<List<Guid>> PortalsAsync(ApplicationDbContext context, CancellationToken cancellationToken) =>
        await context.Customers.AsNoTracking()
            .Where(customer => customer.LinkedTenantId != null)
            .OrderBy(customer => customer.LinkedTenantId)
            .Select(customer => customer.LinkedTenantId!.Value)
            .ToListAsync(cancellationToken);

    internal static async Task QueueCatalogueAsync(ApplicationDbContext context, Guid portalTenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var offered = await context.Services.AsNoTracking()
            .Where(service => service.State == ServiceState.Active && service.OfferedInPortal)
            .OrderBy(service => service.Name)
            .ThenBy(service => service.Id)
            .Select(service => new ServiceOffer(service.CrossTenantId, service.Name, service.Description, service.DefaultSlotCount))
            .ToListAsync(cancellationToken);
        foreach (var offer in offered)
        {
            IntegrationOutbox.Enqueue(context, ServiceOfferSync.OfferedKind, portalTenantId, ServiceOfferSync.CorrelationFor(offer.Service, portalTenantId), ServiceOfferSync.WriteOffer(offer));
        }
    }
}
