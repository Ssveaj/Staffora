using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Customers;

public sealed class CustomersBlockUnitDelete : IDeleteBlocker
{
    public Type EntityType => typeof(OrganizationUnit);

    public IReadOnlyList<Type> Dependents { get; } = [typeof(Customer)];

    public async Task<string?> FindUseAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        return await context.Customers.AsNoTracking().AnyAsync(customer => customer.DefaultUnitId == entity.Id, cancellationToken) ? Reason : null;
    }

    public const string Reason = "A customer's portal jobs go to this unit: choose another unit for them first, or archive the unit instead.";
}
