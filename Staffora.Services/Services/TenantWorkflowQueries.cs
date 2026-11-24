using Staffora.Core.Entities.Tenancy;

namespace Staffora.Services.Services;

public static class TenantWorkflowQueries
{
    public static IQueryable<Tenant> WithWorkflowGraph(this IQueryable<Tenant> query) => query;
}
