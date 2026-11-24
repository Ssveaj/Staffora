using Microsoft.EntityFrameworkCore;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.StateEntity;

public static class TestParentWorkflowQueries
{
    public static IQueryable<TestParent> WithWorkflowGraph(this IQueryable<TestParent> query) =>
        query.Include(parent => parent.Children).ThenInclude(child => child.Grandchildren).Include(parent => parent.Attributes);
}
