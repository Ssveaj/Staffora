using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Integration;

public interface IJobStateSync
{
    Task QueueStateAsync(DbContext store, Job job, JobState from, CancellationToken cancellationToken);

    Task QueueStaffMembersAsync(DbContext store, SlotAssignment assignment, CancellationToken cancellationToken);

    Task QueueAnswerAsync(DbContext store, Job job, CancellationToken cancellationToken);
}
