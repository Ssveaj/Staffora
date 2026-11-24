using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Organization;

namespace Staffora.Services.Services;

public static class OrganizationUnitWorkflowQueries
{
    public static IQueryable<OrganizationUnit> WithWorkflowGraph(this IQueryable<OrganizationUnit> query) => query
        .Include(unit => unit.Attributes)
        .Include(unit => unit.Parent)
        .Include(unit => unit.Children)
        .AsSplitQuery();
}
