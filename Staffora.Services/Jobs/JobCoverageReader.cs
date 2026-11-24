using Microsoft.EntityFrameworkCore;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Data;

namespace Staffora.Services.Jobs;

public static class JobCoverageReader
{
    public static async Task<IReadOnlyDictionary<Guid, JobCoverageSummary>> ReadJobsAsync(ApplicationDbContext context, IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jobIds);
        if (jobIds.Count == 0)
        {
            return new Dictionary<Guid, JobCoverageSummary>();
        }

        var slots = (await SlotGroupReader.ReadAsync(context, jobIds, cancellationToken)).Where(slot => slot.State == JobSlotState.Active).ToList();
        var holders = await HoldersBySlotAsync(context, [.. slots.Select(slot => slot.SlotId)], cancellationToken);
        return slots.GroupBy(slot => slot.JobId).ToDictionary(
            job => job.Key,
            job => new JobCoverageSummary(job.Count(), JobCoverageRules.OfGroups(SlotGroups.Of(job).Select(group => new SlotGroupCoverageInput(group.Count, group.SlotIds.Sum(id => holders.GetValueOrDefault(id)))))));
    }

    internal static async Task<IReadOnlyDictionary<Guid, int>> HoldersBySlotAsync(ApplicationDbContext context, IReadOnlyCollection<Guid> slotIds, CancellationToken cancellationToken)
    {
        if (slotIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var inService = StaffMembersInService(context);
        return await context.SlotAssignments.AsNoTracking()
            .Where(assignment => slotIds.Contains(assignment.JobSlotId)
                && (assignment.State == SlotAssignmentState.Assigned || assignment.State == SlotAssignmentState.Accepted)
                && inService.Contains(assignment.ResourceId))
            .GroupBy(assignment => assignment.JobSlotId)
            .Select(slot => new { slot.Key, Count = slot.Count() })
            .ToDictionaryAsync(slot => slot.Key, slot => slot.Count, cancellationToken);
    }

    public static IQueryable<Guid> StaffMembersInService(ApplicationDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Resources.AsNoTracking().Where(StaffServiceRules.InService).Select(resource => resource.Id);
    }
}
