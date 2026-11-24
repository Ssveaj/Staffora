using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Services;

public sealed class TenantOwnerGuard : ITenantOwnerGuard, IStepPreparation
{
    private readonly Dictionary<Guid, bool> hasOwner = [];
    private readonly Lock gate = new();

    public Type EntityType => typeof(Tenant);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var hasOwner = await SystemOwners.ActiveOwnerIds(context, entityId).AnyAsync(cancellationToken);
        lock (this.gate)
        {
            this.hasOwner[entityId] = hasOwner;
        }
    }

    public bool HasActiveSystemOwner(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        lock (this.gate)
        {
            return this.hasOwner.TryGetValue(tenant.Id, out var hasOwner)
                ? hasOwner
                : throw new InvalidOperationException($"Nothing was read about tenant {tenant.Id} before its steps were judged, so its system owner cannot be checked.");
        }
    }
}
