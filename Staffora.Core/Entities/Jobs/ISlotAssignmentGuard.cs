using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;

namespace Staffora.Core.Entities.Jobs;

public interface ISlotAssignmentGuard
{
    StaffingVerdict VerdictOf(SlotAssignment assignment);

    ScheduledAbsence? BlockingAbsenceOf(SlotAssignment assignment);
}
