using Staffora.Core.Entities.Resources;

namespace Staffora.Services.Resources;

public static class QualificationWorkflowQueries
{
    public static IQueryable<Qualification> WithWorkflowGraph(this IQueryable<Qualification> query) => query;
}
