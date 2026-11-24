using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Scheduling;

public static class AvailabilityRules
{
    public const string AssignedReason = "Assigned to work a job";

    public const string AcceptedReason = "Said yes to working a job";

    public const string RequestedReason = "Asked to work a job, not answered yet";

    public static IReadOnlyDictionary<Guid, StaffAvailability> Compute(
        IEnumerable<Guid> staffMembers,
        IEnumerable<ScheduledAssignment> assignments,
        IReadOnlyDictionary<Guid, PatternShape> patterns,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(staffMembers);
        return Compute(staffMembers.Select(staff => new ScheduledStaff(staff, InService: true)), assignments, patterns, [], from, to, zone);
    }

    public static IReadOnlyDictionary<Guid, StaffAvailability> Compute(
        IEnumerable<ScheduledStaff> staffMembers,
        IEnumerable<ScheduledAssignment> assignments,
        IReadOnlyDictionary<Guid, PatternShape> patterns,
        IEnumerable<ScheduledAbsence> absences,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo zone)
    {
        return Compute(staffMembers, assignments, patterns, absences, [], from, to, zone);
    }

    public static IReadOnlyDictionary<Guid, StaffAvailability> Compute(
        IEnumerable<ScheduledStaff> staffMembers,
        IEnumerable<ScheduledAssignment> assignments,
        IReadOnlyDictionary<Guid, PatternShape> patterns,
        IEnumerable<ScheduledAbsence> absences,
        IEnumerable<ScheduledBooking> bookings,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo zone)
    {
        return Compute(staffMembers, assignments, patterns, absences, bookings, [], from, to, zone);
    }

    public static IReadOnlyDictionary<Guid, StaffAvailability> Compute(
        IEnumerable<ScheduledStaff> staffMembers,
        IEnumerable<ScheduledAssignment> assignments,
        IReadOnlyDictionary<Guid, PatternShape> patterns,
        IEnumerable<ScheduledAbsence> absences,
        IEnumerable<ScheduledBooking> bookings,
        IEnumerable<ScheduledClosedDay> closedDays,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(staffMembers);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(absences);
        ArgumentNullException.ThrowIfNull(bookings);
        ArgumentNullException.ThrowIfNull(closedDays);
        ArgumentNullException.ThrowIfNull(zone);
        var closed = closedDays.Where(closedDay => closedDay.FirstDay <= to && closedDay.LastDay >= from && closedDay.LastDay >= closedDay.FirstDay).ToList();
        var byStaff = assignments.ToLookup(assignment => assignment.ResourceId);
        var booked = bookings.ToLookup(booking => booking.ResourceId);
        var absent = absences.Where(absence => AbsenceRules.Blocks(absence.Kind, absence.State)).ToLookup(absence => absence.ResourceId);
        var inService = new Dictionary<Guid, bool>();
        var unitsOf = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var staff in staffMembers)
        {
            inService[staff.ResourceId] = staff.InService && inService.GetValueOrDefault(staff.ResourceId, true);
            (unitsOf.TryGetValue(staff.ResourceId, out var units) ? units : unitsOf[staff.ResourceId] = []).UnionWith(staff.Units);
        }

        var answer = new Dictionary<Guid, StaffAvailability>();
        if (to < from)
        {
            foreach (var staff in inService.Keys)
            {
                answer[staff] = new StaffAvailability(staff, [], [], []);
            }

            return answer;
        }

        var rangeStart = WallClock.ToUtc(from, TimeOnly.MinValue, zone);
        var rangeEnd = to < DateOnly.MaxValue ? WallClock.ToUtc(to.AddDays(1), TimeOnly.MinValue, zone) : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        foreach (var (staff, serving) in inService)
        {
            if (!serving)
            {
                answer[staff] = new StaffAvailability(staff, [], [new AvailabilityBlock(rangeStart, rangeEnd, AbsenceRules.NotInServiceReason)], []);
                continue;
            }

            var (bookedBlocks, warnings) = OccurrencesOf(booked[staff], from, to, zone);
            var blocks = BlocksOf(absent[staff], rangeStart, rangeEnd, zone);
            var applying = closed.Where(closedDay => closedDay.AppliesTo(unitsOf[staff])).ToList();
            var closedBlocks = ClosedBlocksOf(applying, rangeStart, rangeEnd, zone);
            if (bookedBlocks.Count > 0 || closedBlocks.Count > 0)
            {
                blocks = [.. blocks.Concat(bookedBlocks).Concat(closedBlocks).OrderBy(block => block.StartUtc).ThenBy(block => block.EndUtc).ThenBy(block => block.SourceId)];
            }

            answer[staff] = new StaffAvailability(staff, WorkingTimeOf(byStaff[staff].ToList(), patterns, from, to, zone), blocks, warnings)
            {
                Closed = ClosedTimesOf(applying, from, to),
            };
        }

        return answer;
    }

    private static List<AvailabilityBlock> ClosedBlocksOf(IEnumerable<ScheduledClosedDay> closedDays, DateTime rangeStart, DateTime rangeEnd, TimeZoneInfo zone)
    {
        var blocks = new List<AvailabilityBlock>();
        foreach (var closedDay in closedDays)
        {
            if (closedDay.FirstDay == DateOnly.MinValue || closedDay.LastDay >= DateOnly.MaxValue)
            {
                continue;
            }

            var (start, end) = ClosedDayRules.Instants(closedDay.FirstDay, closedDay.LastDay, zone);
            var (cutStart, cutEnd) = (start < rangeStart ? rangeStart : start, end > rangeEnd ? rangeEnd : end);
            if (cutEnd > cutStart)
            {
                blocks.Add(new AvailabilityBlock(cutStart, cutEnd, ClosedDayRules.BlockReason(closedDay.Name), closedDay.ClosedDayId));
            }
        }

        return blocks;
    }

    private static List<ClosedTime> ClosedTimesOf(IEnumerable<ScheduledClosedDay> closedDays, DateOnly from, DateOnly to)
    {
        var times = new List<ClosedTime>();
        foreach (var closedDay in closedDays)
        {
            var first = closedDay.FirstDay < from ? from : closedDay.FirstDay;
            var last = closedDay.LastDay > to ? to : closedDay.LastDay;
            for (var date = first; date <= last; date = date.AddDays(1))
            {
                times.Add(new ClosedTime(date, closedDay.ClosedDayId, closedDay.Name));
                if (date == DateOnly.MaxValue)
                {
                    break;
                }
            }
        }

        return [.. times.OrderBy(time => time.Date).ThenBy(time => time.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(time => time.ClosedDayId)];
    }

    private static (List<AvailabilityBlock> Blocks, List<AvailabilityWarning> Warnings) OccurrencesOf(IEnumerable<ScheduledBooking> bookings, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var blocks = new List<AvailabilityBlock>();
        var warnings = new List<AvailabilityWarning>();
        foreach (var booking in bookings)
        {
            var reason = booking.State switch
            {
                SlotAssignmentState.Assigned => AssignedReason,
                SlotAssignmentState.Accepted => AcceptedReason,
                SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion => RequestedReason,
                _ => null,
            };
            if (reason is null)
            {
                continue;
            }

            foreach (var occurrence in JobRules.Occurrences(booking.Times, from, to, zone))
            {
                if (booking.State is SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion)
                {
                    warnings.Add(new AvailabilityWarning(occurrence.StartUtc, occurrence.EndUtc, reason, booking.AssignmentId));
                }
                else
                {
                    blocks.Add(new AvailabilityBlock(occurrence.StartUtc, occurrence.EndUtc, reason, booking.AssignmentId));
                }
            }
        }

        return ([.. blocks.OrderBy(block => block.StartUtc).ThenBy(block => block.SourceId)], [.. warnings.OrderBy(warning => warning.StartUtc).ThenBy(warning => warning.SourceId)]);
    }

    private static List<AvailabilityBlock> BlocksOf(IEnumerable<ScheduledAbsence> absences, DateTime rangeStart, DateTime rangeEnd, TimeZoneInfo zone)
    {
        var blocks = new List<AvailabilityBlock>();
        foreach (var absence in absences)
        {
            if (AbsenceRules.Problems(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime).Contains(AbsenceRules.OutOfCalendar))
            {
                continue;
            }

            var (start, end) = AbsenceRules.Instants(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime, zone);
            var (cutStart, cutEnd) = (start < rangeStart ? rangeStart : start, end > rangeEnd ? rangeEnd : end);
            if (cutEnd > cutStart)
            {
                blocks.Add(new AvailabilityBlock(cutStart, cutEnd, AbsenceRules.BlockReason(absence.Kind, absence.State), absence.AbsenceId));
            }
        }

        return [.. blocks.OrderBy(block => block.StartUtc).ThenBy(block => block.EndUtc).ThenBy(block => block.SourceId)];
    }

    private static List<WorkingTime> WorkingTimeOf(List<ScheduledAssignment> assignments, IReadOnlyDictionary<Guid, PatternShape> patterns, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var working = new List<WorkingTime>();
        if (assignments.Count == 0 || to < from)
        {
            return working;
        }

        var byId = assignments.ToDictionary(assignment => assignment.AssignmentId);
        var dates = assignments.Select(assignment => assignment.Dates).ToList();
        ScheduledAssignment? running = null;
        var runStart = from;
        for (var date = from; ; date = date.AddDays(1))
        {
            var applies = ScheduleRules.AssignmentOn(date, dates) is { } chosen ? byId[chosen.AssignmentId] : null;
            if (!ReferenceEquals(applies, running))
            {
                if (running is not null)
                {
                    AddRun(working, running, patterns, runStart, date.AddDays(-1), zone);
                }

                running = applies;
                runStart = date;
            }

            if (date == to)
            {
                break;
            }
        }

        if (running is not null)
        {
            AddRun(working, running, patterns, runStart, to, zone);
        }

        return working;
    }

    private static void AddRun(List<WorkingTime> working, ScheduledAssignment assignment, IReadOnlyDictionary<Guid, PatternShape> patterns, DateOnly first, DateOnly last, TimeZoneInfo zone)
    {
        if (!patterns.TryGetValue(assignment.WorkPatternId, out var pattern))
        {
            throw new InvalidOperationException($"The pattern {assignment.WorkPatternId} of the assignment {assignment.AssignmentId} was not read with it.");
        }

        foreach (var segment in WorkPatternRules.Expand(pattern.CycleWeeks, pattern.Segments, assignment.CycleAnchor, first, last))
        {
            var startUtc = WallClock.ToUtc(segment.Date, segment.Start, zone);
            var endUtc = WallClock.ToUtc(segment.Date, segment.End, zone);
            if (endUtc <= startUtc)
            {
                continue;
            }

            working.Add(new WorkingTime(segment.Date, WallClock.LocalTimeOf(startUtc, zone), WallClock.LocalTimeOf(endUtc, zone), startUtc, endUtc, assignment.AssignmentId, assignment.WorkPatternId));
        }
    }
}
