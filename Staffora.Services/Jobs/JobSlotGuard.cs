using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed class JobSlotGuard : IJobSlotGuard, IStepPreparation
{
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> stored = [];
    private readonly Dictionary<Guid, Guid?> storedServices = [];
    private readonly Lock gate = new();

    public Type EntityType => typeof(JobSlot);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stored = await context.JobSlotQualifications.AsNoTracking()
            .Where(required => required.JobSlotId == entityId)
            .Select(required => required.QualificationId)
            .ToListAsync(cancellationToken);
        var service = await context.JobSlots.AsNoTracking().Where(slot => slot.Id == entityId).Select(slot => slot.ServiceId).FirstOrDefaultAsync(cancellationToken);
        lock (this.gate)
        {
            this.stored[entityId] = stored.ToHashSet();
            this.storedServices[entityId] = service;
        }
    }

    public IReadOnlySet<Guid> StoredQualifications(JobSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        lock (this.gate)
        {
            return this.stored.TryGetValue(slot.Id, out var stored) ? stored : new HashSet<Guid>();
        }
    }

    public Guid? StoredService(JobSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        lock (this.gate)
        {
            return this.storedServices.TryGetValue(slot.Id, out var service) ? service : null;
        }
    }
}
