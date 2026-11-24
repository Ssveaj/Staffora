using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;

namespace Staffora.Services.Catalogue;

public static class ServiceWorkflowQueries
{
    public static IQueryable<Service> WithWorkflowGraph(this IQueryable<Service> query) => query
        .Include(service => service.Qualifications).ThenInclude(named => named.Qualification);
}
