using Staffora.Core.Entities.Customers;

namespace Staffora.Services.Customers;

public static class CustomerWorkflowQueries
{
    public static IQueryable<Customer> WithWorkflowGraph(this IQueryable<Customer> query) => query;
}
