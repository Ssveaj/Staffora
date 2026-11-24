using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;

namespace Staffora.Services.Services;

public static class ApplicationUserWorkflowQueries
{
    public static IQueryable<ApplicationUser> WithWorkflowGraph(this IQueryable<ApplicationUser> query) => query.Include(user => user.Attributes);
}
