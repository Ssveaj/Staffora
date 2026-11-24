using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Resources;

public sealed class ResourceMembersBlockUnitDelete : IDeleteBlocker
{
    public Type EntityType => typeof(OrganizationUnit);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(ResourceUnitMembership)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        return await context.ResourceUnitMemberships.AsNoTracking().AnyAsync(membership => membership.OrganizationUnitId == entity.Id, cancellationToken) ? Reason : null;
    }

    public const string Reason = "Staff belong to this unit: take their staff role on it away first, or archive the unit instead.";
}
