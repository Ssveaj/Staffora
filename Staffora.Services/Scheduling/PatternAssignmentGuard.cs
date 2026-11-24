using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Scheduling;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Scheduling;

public sealed class PatternAssignmentGuard : IPatternAssignmentGuard, IStepPreparation
{
    private readonly Dictionary<Guid, IReadOnlyList<ScheduledDates>> others = [];
    private readonly Lock gate = new();

    public Type EntityType => typeof(ResourcePatternAssignment);

    public async Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourceId = await context.ResourcePatternAssignments.AsNoTracking()
            .Where(assignment => assignment.Id == entityId)
            .Select(assignment => (Guid?)assignment.ResourceId)
            .FirstOrDefaultAsync(cancellationToken);
        IReadOnlyList<ScheduledDates> others = resourceId is { } staff
            ? [.. (await context.ResourcePatternAssignments.AsNoTracking()
                    .Where(assignment => assignment.ResourceId == staff && assignment.Id != entityId && assignment.State == ResourcePatternAssignmentState.Active)
                    .Select(assignment => new { assignment.Id, assignment.EffectiveFrom, assignment.EffectiveTo, assignment.WorkPattern.Name })
                    .ToListAsync(cancellationToken))
                .Select(row => new ScheduledDates(row.Id, row.EffectiveFrom, row.EffectiveTo, row.Name))]
            : [];
        lock (this.gate)
        {
            this.others[entityId] = others;
        }
    }

    public IReadOnlyList<ScheduledDates> OtherActiveAssignmentsOf(ResourcePatternAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        lock (this.gate)
        {
            return this.others.TryGetValue(assignment.Id, out var others)
                ? others
                : throw new InvalidOperationException($"Nothing was read about the pattern assignment {assignment.Id} before its steps were judged, so its staff member's other assignments cannot be checked.");
        }
    }
}
