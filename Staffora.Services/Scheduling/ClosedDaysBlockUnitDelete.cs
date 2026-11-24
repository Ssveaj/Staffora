using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Scheduling;

public sealed class ClosedDaysBlockUnitDelete : IDeleteBlocker
{
    public Type EntityType => typeof(OrganizationUnit);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(ClosedDay)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        return await context.ClosedDays.AsNoTracking().AnyAsync(closedDay => closedDay.OrganizationUnitId == entity.Id, cancellationToken) ? Reason : null;
    }

    public const string Reason = "Closed days are kept for this unit: delete them under the settings' Closed days first, or archive the unit instead.";
}
