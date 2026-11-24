using Microsoft.EntityFrameworkCore;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

public static class TestChildWorkflowQueries
{
    public static IQueryable<TestChild> WithWorkflowGraph(this IQueryable<TestChild> query) =>
        query.Include(child => child.Parent);
}
