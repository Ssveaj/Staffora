using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Services.Scheduling;

public static class WorkPatternWorkflowQueries
{
    public static IQueryable<WorkPattern> WithWorkflowGraph(this IQueryable<WorkPattern> query) => query.Include(pattern => pattern.Segments);
}
