using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Portal;

namespace Staffora.Services.Portal;

public static class OrderWorkflowQueries
{
    public static IQueryable<Order> WithWorkflowGraph(this IQueryable<Order> query) => query
        .Include(request => request.Supplier)
        .Include(request => request.Lines).ThenInclude(line => line.OfferedService);
}
