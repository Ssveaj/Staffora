using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Scheduling;

public sealed record ScheduledStaff(Guid ResourceId, bool InService)
{
    public IReadOnlySet<Guid> Units { get; init; } = new HashSet<Guid>();
}

public sealed record ScheduledAssignment(Guid AssignmentId, Guid ResourceId, Guid WorkPatternId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, DateOnly CycleAnchor)
{
    public ScheduledDates Dates => new(AssignmentId, EffectiveFrom, EffectiveTo);
}

public sealed record ScheduledBooking(Guid AssignmentId, Guid ResourceId, SlotAssignmentState State, JobTimes Times)
{
    public ScheduledBooking(Guid assignmentId, Guid resourceId, SlotAssignmentState state, DateOnly periodStart, DateOnly periodEnd, IReadOnlyList<OccurrenceTimes> occurrences)
        : this(assignmentId, resourceId, state, JobTimes.Recurring(periodStart, periodEnd, occurrences))
    {
    }
}

public sealed record PatternShape(Guid WorkPatternId, int CycleWeeks, IReadOnlyList<PatternSegment> Segments);

public sealed record WorkingTime(DateOnly Date, TimeOnly LocalStart, TimeOnly LocalEnd, DateTime StartUtc, DateTime EndUtc, Guid AssignmentId, Guid WorkPatternId)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

public sealed record AvailabilityBlock(DateTime StartUtc, DateTime EndUtc, string Reason, Guid? SourceId = null);

public sealed record AvailabilityWarning(DateTime StartUtc, DateTime EndUtc, string Reason, Guid? SourceId = null);

public sealed record StaffAvailability(Guid ResourceId, IReadOnlyList<WorkingTime> Working, IReadOnlyList<AvailabilityBlock> Blocks, IReadOnlyList<AvailabilityWarning> Warnings)
{
    public IReadOnlyList<ClosedTime> Closed { get; init; } = [];

    public IEnumerable<ClosedTime> ClosedOn(DateOnly date) => Closed.Where(closed => closed.Date == date);

    public IEnumerable<WorkingTime> On(DateOnly date) => Working.Where(segment => segment.Date == date);

    public bool IsWithinWorkingTime(DateTime startUtc, DateTime endUtc) => IsWithin([.. Working.OrderBy(segment => segment.StartUtc)], startUtc, endUtc);

    public static bool IsWithin(IReadOnlyList<WorkingTime> ordered, DateTime startUtc, DateTime endUtc)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (endUtc <= startUtc)
        {
            return false;
        }

        DateTime? stretchStart = null;
        DateTime stretchEnd = default;
        foreach (var segment in ordered)
        {
            if (stretchStart is null || segment.StartUtc > stretchEnd)
            {
                if (stretchStart is { } from && from <= startUtc && endUtc <= stretchEnd)
                {
                    return true;
                }

                if (segment.StartUtc > startUtc)
                {
                    return false;
                }

                stretchStart = segment.StartUtc;
                stretchEnd = segment.EndUtc;
            }
            else if (segment.EndUtc > stretchEnd)
            {
                stretchEnd = segment.EndUtc;
            }
        }

        return stretchStart is { } last && last <= startUtc && endUtc <= stretchEnd;
    }

    public IReadOnlyList<WorkingTime> OrderedWorking() => [.. Working.OrderBy(segment => segment.StartUtc)];
}

public sealed record AvailabilityAnswer(DateOnly From, DateOnly To, string TimeZoneId, IReadOnlyDictionary<Guid, StaffAvailability> StaffMembers);
