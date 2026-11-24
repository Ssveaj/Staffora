using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Data;

namespace Staffora.Services.Scheduling;

internal static class StaffPatternCoverage
{
    public static IQueryable<Resource> WithCurrent(this IQueryable<Resource> resources, ApplicationDbContext context, DateOnly today) =>
        resources.Where(resource => context.ResourcePatternAssignments.Any(assignment => assignment.ResourceId == resource.Id
            && assignment.State == ResourcePatternAssignmentState.Active
            && assignment.EffectiveFrom <= today
            && (assignment.EffectiveTo == null || assignment.EffectiveTo >= today)));

    public static IQueryable<Resource> WithoutPattern(this IQueryable<Resource> resources, ApplicationDbContext context, DateOnly today) =>
        resources.Where(StaffServiceRules.SetUpOpen).Where(resource => !context.ResourcePatternAssignments.Any(assignment => assignment.ResourceId == resource.Id
            && assignment.State == ResourcePatternAssignmentState.Active
            && (assignment.EffectiveTo == null || assignment.EffectiveTo >= today)));

    public static IQueryable<Resource> EndingSoon(this IQueryable<Resource> resources, ApplicationDbContext context, DateOnly today)
    {
        var soon = today.AddDays(StaffPatternCover.EndingSoonDays);
        return resources.Where(StaffServiceRules.SetUpOpen)
            .Where(resource => context.ResourcePatternAssignments.Any(assignment => assignment.ResourceId == resource.Id
                && assignment.State == ResourcePatternAssignmentState.Active
                && assignment.EffectiveFrom <= today
                && assignment.EffectiveTo != null && assignment.EffectiveTo >= today && assignment.EffectiveTo <= soon))
            .Where(resource => !context.ResourcePatternAssignments.Any(assignment => assignment.ResourceId == resource.Id
                && assignment.State == ResourcePatternAssignmentState.Active
                && assignment.EffectiveFrom > today));
    }

    public static Expression<Func<Resource, string?>> PatternName(ApplicationDbContext context, DateOnly today) =>
        resource => context.ResourcePatternAssignments
            .Where(assignment => assignment.ResourceId == resource.Id
                && assignment.State == ResourcePatternAssignmentState.Active
                && (assignment.EffectiveTo == null || assignment.EffectiveTo >= today))
            .OrderBy(assignment => assignment.EffectiveFrom)
            .Select(assignment => assignment.WorkPattern.Name)
            .FirstOrDefault();

    public static async Task<IReadOnlyDictionary<Guid, StaffPatternCover>> CoversAsync(ApplicationDbContext context, IReadOnlyCollection<Guid> resourceIds, DateOnly today, CancellationToken cancellationToken)
    {
        if (resourceIds.Count == 0)
        {
            return new Dictionary<Guid, StaffPatternCover>();
        }

        var active = await context.ResourcePatternAssignments.AsNoTracking()
            .Where(assignment => resourceIds.Contains(assignment.ResourceId)
                && assignment.State == ResourcePatternAssignmentState.Active
                && (assignment.EffectiveTo == null || assignment.EffectiveTo >= today))
            .Select(assignment => new { assignment.ResourceId, assignment.WorkPattern.Name, assignment.EffectiveFrom, assignment.EffectiveTo })
            .ToListAsync(cancellationToken);
        var covers = new Dictionary<Guid, StaffPatternCover>();
        foreach (var staff in active.GroupBy(assignment => assignment.ResourceId))
        {
            if (StaffPatternCover.Of(staff.Select(assignment => (assignment.Name, assignment.EffectiveFrom, assignment.EffectiveTo)), today) is { } cover)
            {
                covers[staff.Key] = cover;
            }
        }

        return covers;
    }
}
