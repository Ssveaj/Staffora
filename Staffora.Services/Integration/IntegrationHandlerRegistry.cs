using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Integration;
using Staffora.Data;

namespace Staffora.Services.Integration;

public sealed record IntegrationHandlerRegistration(string Kind, Type HandlerType);

public enum IntegrationHandling
{
    Handled = 0,

    AlreadyHandled = 1,
}

public sealed class IntegrationHandlerRegistry
{
    private readonly Dictionary<string, Type> handlers = new(StringComparer.Ordinal);

    public IntegrationHandlerRegistry(IEnumerable<IntegrationHandlerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
        {
            if (string.IsNullOrWhiteSpace(registration.Kind) || registration.Kind.Length > IntegrationMessage.MaxKindLength)
            {
                throw new InvalidOperationException($"An integration handler's kind is 1 to {IntegrationMessage.MaxKindLength} characters; {registration.HandlerType.Name} has \"{registration.Kind}\".");
            }

            if (!typeof(IIntegrationHandler).IsAssignableFrom(registration.HandlerType))
            {
                throw new InvalidOperationException($"{registration.HandlerType.Name} handles no integration message.");
            }

            if (!this.handlers.TryAdd(registration.Kind, registration.HandlerType))
            {
                throw new InvalidOperationException($"The integration message kind \"{registration.Kind}\" has two handlers: {this.handlers[registration.Kind].Name} and {registration.HandlerType.Name}.");
            }
        }
    }

    public IReadOnlyCollection<string> Kinds => this.handlers.Keys;

    public bool Handles(string kind) => this.handlers.ContainsKey(kind);

    public async Task<IntegrationHandling> HandleOnceAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(context);
        if (!this.handlers.TryGetValue(delivery.Kind, out var handlerType))
        {
            throw new InvalidOperationException($"No integration handler takes the kind \"{delivery.Kind}\".");
        }

        var (messageId, tenantId) = (delivery.MessageId, delivery.TargetTenantId);
        if (await context.IntegrationReceipts.AsNoTracking().AnyAsync(receipt => receipt.TenantId == tenantId && receipt.MessageId == messageId, cancellationToken))
        {
            return IntegrationHandling.AlreadyHandled;
        }

        context.IntegrationReceipts.Add(new IntegrationReceipt { TenantId = tenantId, MessageId = messageId, Kind = delivery.Kind });
        var handler = (IIntegrationHandler)delivery.Services.GetRequiredService(handlerType);
        await handler.HandleAsync(delivery, context, cancellationToken);
        return IntegrationHandling.Handled;
    }
}
