using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Services.Services;

public static class TenantRoleWorkflowQueries
{
    public static IQueryable<TenantRole> WithWorkflowGraph(this IQueryable<TenantRole> query) => query.Include(role => role.Permissions);
}
