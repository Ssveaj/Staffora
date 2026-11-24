using Staffora.Core.Models;
using Staffora.Core.Scheduling;

namespace Staffora.Services.Scheduling;

public interface IStaffAvailability
{
    Task<Result<AvailabilityAnswer>> ReadAsync(IReadOnlyCollection<Guid> resourceIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
