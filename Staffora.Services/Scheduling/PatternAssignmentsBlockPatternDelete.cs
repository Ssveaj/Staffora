using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Scheduling;

public sealed class PatternAssignmentsBlockPatternDelete : IDeleteBlocker
{
    public Type EntityType => typeof(WorkPattern);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(ResourcePatternAssignment)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        return await context.ResourcePatternAssignments.AsNoTracking().AnyAsync(assignment => assignment.WorkPatternId == entity.Id, cancellationToken) ? Reason : null;
    }

    public const string Reason = "Staff are given this pattern, so it is kept for their schedules. Archive it instead, so nobody new is given it.";
}
