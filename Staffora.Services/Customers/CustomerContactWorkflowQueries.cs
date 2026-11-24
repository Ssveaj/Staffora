using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Customers;

namespace Staffora.Services.Customers;

public static class CustomerContactWorkflowQueries
{
    public static IQueryable<CustomerContact> WithWorkflowGraph(this IQueryable<CustomerContact> query) => query.Include(contact => contact.Customer);
}
