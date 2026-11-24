using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Scheduling;

namespace Staffora.Services.Scheduling;

public static class ResourceAbsenceWorkflowQueries
{
    public static IQueryable<ResourceAbsence> WithWorkflowGraph(this IQueryable<ResourceAbsence> query) =>
        query.Include(absence => absence.Resource).ThenInclude(resource => resource.User);
}
