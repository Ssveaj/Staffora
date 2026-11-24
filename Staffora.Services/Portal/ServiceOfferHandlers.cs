using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Integration;
using Staffora.Data;
using Staffora.Services.Caching;
using Staffora.Services.Integration;

namespace Staffora.Services.Portal;

public sealed class ServiceOfferedHandler : IIntegrationHandler
{
    private readonly ILogger<ServiceOfferedHandler> log;

    public ServiceOfferedHandler(ILogger<ServiceOfferedHandler> log)
    {
        this.log = log;
    }

    public Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken) =>
        OfferedServiceCopy.ApplyAsync(delivery, context, this.log, cancellationToken);
}

public sealed class ServiceChangedHandler : IIntegrationHandler
{
    private readonly ILogger<ServiceChangedHandler> log;

    public ServiceChangedHandler(ILogger<ServiceChangedHandler> log)
    {
        this.log = log;
    }

    public Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken) =>
        OfferedServiceCopy.ApplyAsync(delivery, context, this.log, cancellationToken);
}

public sealed class ServiceWithdrawnHandler : IIntegrationHandler
{
    private readonly ILogger<ServiceWithdrawnHandler> log;

    public ServiceWithdrawnHandler(ILogger<ServiceWithdrawnHandler> log)
    {
        this.log = log;
    }

    public Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken) =>
        OfferedServiceCopy.ApplyAsync(delivery, context, this.log, cancellationToken);
}

internal static class OfferedServiceCopy
{
    public static async Task ApplyAsync(IntegrationDelivery delivery, ApplicationDbContext context, ILogger log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(context);
        var withdrawn = delivery.Kind == ServiceOfferSync.WithdrawnKind;
        var offer = withdrawn ? null : ServiceOfferSync.ReadOffer(delivery.Payload);
        var link = withdrawn ? ServiceOfferSync.ReadWithdrawal(delivery.Payload) : offer?.Service;
        if (link is not { } serviceLink)
        {
            log.LogWarning("The service message {MessageId} of tenant {SourceTenantId} names no service; it changes nothing.", delivery.MessageId, delivery.SourceTenantId);
            return;
        }

        var source = delivery.SourceTenantId;
        var supplierId = await context.Suppliers.AsNoTracking()
            .Where(supplier => supplier.LinkedTenantId == source)
            .Select(supplier => (Guid?)supplier.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"The portal {delivery.TargetTenantId} has no supplier party for the tenant that offered a service.");

        var copy = await context.OfferedServices.AsTracking()
            .FirstOrDefaultAsync(row => row.SupplierId == supplierId && row.LinkedCrossTenantId == serviceLink, cancellationToken);
        if (offer is null)
        {
            if (copy is not null)
            {
                copy.Offered = false;
            }
        }
        else
        {
            if (copy is null)
            {
                copy = new OfferedService { TenantId = delivery.TargetTenantId, SupplierId = supplierId, LinkedCrossTenantId = serviceLink };
                context.OfferedServices.Add(copy);
            }

            copy.Name = Clip(offer.Name, OfferedService.NameMaxLength);
            copy.Description = offer.Description is { } text ? Clip(text, OfferedService.DescriptionMaxLength) : null;
            copy.DefaultSlotCount = Math.Clamp(offer.DefaultSlotCount, OrderLine.MinCount, OrderLine.MaxCount);
            copy.Offered = true;
        }

        var cache = delivery.Services.GetRequiredService<TenantLookupCache>();
        var tenant = delivery.TargetTenantId;
        delivery.AfterCommit.Add(_ =>
        {
            cache.Invalidate(OrderService.OfferedCatalogue, tenant);
            return Task.CompletedTask;
        });
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}
