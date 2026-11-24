using Staffora.Core.Entities;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow;

namespace Staffora.Data;

public static class StateHistoryWriter
{
    public static IReadOnlyList<StateHistoryEntry> Add(ApplicationDbContext context, TransitionOutcome outcome, Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return Add(context, outcome.Steps, correlationId);
    }

    public static IReadOnlyList<StateHistoryEntry> Add(ApplicationDbContext context, IReadOnlyList<TransitionStep> steps, Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return [];
        }

        if (context.RuntimeContext is not { EffectiveUserId: { } actor } runtime)
        {
            throw new InvalidOperationException("A transition is recorded with the user who ran it, and the context's runtime has none.");
        }

        var actorsTenant = runtime.TenantId;

        var entries = steps.Select(step => new StateHistoryEntry
        {
            TenantId = TenantOf(step.Entity),
            EntityType = EntityTypeName(context, step.Entity),
            EntityId = step.Entity.Id,
            TransitionKey = step.TransitionKey,
            FromState = Convert.ToInt32(step.From),
            ToState = Convert.ToInt32(step.To),
            ActorId = actor,
        }).ToList();

        foreach (var entry in entries.Where(entry => entry.TenantId != Guid.Empty && entry.TenantId != actorsTenant))
        {
            entry.CreatedById = SystemAccount.IdFor(entry.TenantId);
        }

        context.StateHistoryEntries.AddRange(entries);
        var correlation = correlationId ?? entries[0].Id;
        foreach (var entry in entries)
        {
            entry.CorrelationId = correlation;
        }

        return entries;
    }

    public static IReadOnlyList<StateHistoryEntry> AddOnBehalfOf(ApplicationDbContext context, Guid tenantId, IReadOnlyList<TransitionStep> steps)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return [];
        }

        var account = SystemAccount.IdFor(tenantId);
        var entries = steps.Select(step => TenantOf(step.Entity) == tenantId
            ? new StateHistoryEntry
            {
                TenantId = tenantId,
                EntityType = EntityTypeName(context, step.Entity),
                EntityId = step.Entity.Id,
                TransitionKey = step.TransitionKey,
                FromState = Convert.ToInt32(step.From),
                ToState = Convert.ToInt32(step.To),
                ActorId = account,
                CreatedById = account,
            }
            : throw new InvalidOperationException("A write on a tenant's behalf records the history of that tenant's rows only.")).ToList();

        context.StateHistoryEntries.AddRange(entries);
        foreach (var entry in entries)
        {
            entry.CorrelationId = entries[0].Id;
        }

        return entries;
    }

    private static Guid TenantOf(IStateEntity entity) => entity switch
    {
        ITenantEntity row => row.TenantId,
        Tenant tenant => tenant.Id,
        IEntityAttributeDefinition field => field.TenantId ?? Guid.Empty,
        _ => throw new NotSupportedException($"{entity.GetType().Name} is neither a tenant row nor a tenant, so its history has no tenant."),
    };

    private static string EntityTypeName(ApplicationDbContext context, IStateEntity entity) =>
        (context.Model.FindRuntimeEntityType(entity.GetType())?.ClrType ?? entity.GetType()).Name;
}
