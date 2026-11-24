using Staffora.Core.Scheduling;

namespace Staffora.Core.Entities.Scheduling;

public interface IPatternAssignmentGuard
{
    IReadOnlyList<ScheduledDates> OtherActiveAssignmentsOf(ResourcePatternAssignment assignment);
}
