using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Services.Scheduling;

public static class ResourcePatternAssignmentWorkflowQueries
{
    public static IQueryable<ResourcePatternAssignment> WithWorkflowGraph(this IQueryable<ResourcePatternAssignment> query) =>
        query.Include(assignment => assignment.Resource).Include(assignment => assignment.WorkPattern);
}
