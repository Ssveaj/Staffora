using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Catalogue;

public sealed class ServiceGuard : IServiceGuard, IStepPreparation
{
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> stored = [];
    private readonly Dictionary<Guid, StoredOffer> offers = [];
    private readonly Lock gate = new();

    public Type EntityType => typeof(Service);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stored = await context.ServiceQualifications.AsNoTracking()
            .Where(named => named.ServiceId == entityId)
            .Select(named => named.QualificationId)
            .ToListAsync(cancellationToken);
        var offer = await context.Services.AsNoTracking()
            .Where(service => service.Id == entityId)
            .Select(service => new StoredOffer(service.State, service.OfferedInPortal, service.Name, service.Description, service.DefaultSlotCount))
            .FirstOrDefaultAsync(cancellationToken);
        lock (this.gate)
        {
            this.stored[entityId] = stored.ToHashSet();
            if (offer is not null)
            {
                this.offers[entityId] = offer;
            }
        }
    }

    internal StoredOffer? StoredOfferOf(Guid serviceId)
    {
        lock (this.gate)
        {
            return this.offers.TryGetValue(serviceId, out var offer) ? offer : null;
        }
    }

    public IReadOnlySet<Guid> StoredQualifications(Service service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (this.gate)
        {
            return this.stored.TryGetValue(service.Id, out var stored) ? stored : new HashSet<Guid>();
        }
    }
}

internal sealed record StoredOffer(ServiceState State, bool OfferedInPortal, string Name, string? Description, int DefaultSlotCount);
