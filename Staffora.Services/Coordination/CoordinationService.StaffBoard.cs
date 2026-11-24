using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Scheduling;

namespace Staffora.Services.Coordination;

public sealed partial class CoordinationService
{
    public const string BoardReadFailed = "The staff members' week could not be read. Try again.";

    public const string WeekOutOfCalendar = StaffAvailabilityService.RangeOutOfCalendar;

    public const string NotThisTenant = "The board was read for another organization. Read it again.";

    public Task<Result<BoardStaffMembers>> ReadStaffMembersAsync(DateOnly? week, BoardFilter filter, CancellationToken cancellationToken = default) =>
        ReadStaffMembersAsync(week, filter, 1, cancellationToken);

    public async Task<Result<BoardStaffMembers>> ReadStaffMembersAsync(DateOnly? week, BoardFilter filter, int page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (await RefusalAsync(cancellationToken) is { } refusal)
            return Result<BoardStaffMembers>.Failure(refusal);

        if (week is { } asked && !ScheduleRules.HasReadableWeek(asked))
            return Result<BoardStaffMembers>.Failure(WeekOutOfCalendar);

        var tenantId = this.runtime.TenantId!.Value;
        try
        {
            return await ReadStaffMembersOfTenantAsync(tenantId, week, filter, page, cancellationToken);
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "the staff week board", cancellationToken);
            this.log.LogError(exception, "Reading the staff week board of tenant {TenantId} failed.", tenantId);
            return Result<BoardStaffMembers>.Failure(BoardReadFailed);
        }
    }

    public async Task<Result<BoardBookings>> ReadBookingsAsync(BoardStaffMembers staffMembers, CoordinationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staffMembers);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (await RefusalAsync(cancellationToken) is { } refusal)
            return Result<BoardBookings>.Failure(refusal);

        var tenantId = this.runtime.TenantId!.Value;
        if (staffMembers.TenantId != tenantId || snapshot.TenantId != tenantId)
            return Result<BoardBookings>.Failure(NotThisTenant);

        try
        {
            return await ReadBookingsOfTenantAsync(tenantId, staffMembers, snapshot, cancellationToken);
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "the staff week board's bookings", cancellationToken);
            this.log.LogError(exception, "Reading the bookings of the staff week board of tenant {TenantId} failed.", tenantId);
            return Result<BoardBookings>.Failure(BoardReadFailed);
        }
    }

    private async Task<string?> RefusalAsync(CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return NoView;

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return CoordinationService.NotOnConsole;

        return session.TenantId is null ? IRuntimeExecutionContext.NoTenant : null;
    }

    private async Task<Result<BoardStaffMembers>> ReadStaffMembersOfTenantAsync(Guid tenantId, DateOnly? week, BoardFilter filter, int page, CancellationToken cancellationToken)
    {
        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<BoardStaffMembers>.Failure(zone.Error);

        var today = JobRules.LocalDay(this.clock.GetUtcNow(), timeZone);
        var days = BoardRules.Days(week ?? today);
        var (monday, sunday) = (days[0], days[^1]);
        var mayAddAbsence = await this.permissionService.HasPermissionAsync(RolePermission.ManageResourceAbsence, cancellationToken);
        var reader = this.runtime.Current.EffectiveUserId;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);

        var records = (await context.Resources.AsNoTracking()
                .Where(resource => resource.TenantId == tenantId)
                .Where(StaffServiceRules.InService)
                .Select(resource => new { resource.Id, resource.UserId, Name = (resource.User.FirstName + " " + resource.User.LastName).Trim(), resource.User.Email })
                .ToListAsync(cancellationToken))
            .Select(record => (record.Id, record.UserId, Name: record.Name.Length > 0 ? record.Name : record.Email ?? "Unknown staff member"))
            .ToList();

        var name = filter.NameText;
        var narrowed = records
            .Where(record => name.Length == 0 || record.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase));
        var filtered = (filter.NameDescending
                ? narrowed.OrderByDescending(record => record.Name, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(record => record.Id)
                : narrowed.OrderBy(record => record.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(record => record.Id))
            .ToList();
        var shownPage = BoardRules.PageWithin(page, filtered.Count);
        var shown = filtered.Skip((shownPage - 1) * BoardRules.PageSize).Take(BoardRules.PageSize).ToList();
        var ids = shown.Select(record => record.Id).ToList();

        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> held = new Dictionary<Guid, IReadOnlySet<Guid>>();
        IReadOnlyDictionary<Guid, StaffAvailability> working = new Dictionary<Guid, StaffAvailability>();
        var absences = new List<(Guid Id, Guid ResourceId, AbsenceKind Kind, ResourceAbsenceState State, DateOnly FirstDay, TimeOnly? StartTime, DateOnly LastDay, TimeOnly? EndTime)>();
        if (ids.Count > 0)
        {
            var read = await this.qualifications.ReadQualificationsOfStaffMembersAsync(ids, cancellationToken);
            if (!read.IsSuccess || read.Value is null)
                return Result<BoardStaffMembers>.Failure(read.Error);

            held = read.Value;
            var answer = await this.availability.ReadAsync(ids, monday, sunday, cancellationToken);
            if (!answer.IsSuccess || answer.Value is null)
                return Result<BoardStaffMembers>.Failure(answer.Error);

            working = answer.Value.StaffMembers;
            absences = [.. (await context.ResourceAbsences.AsNoTracking()
                    .Where(absence => absence.TenantId == tenantId
                        && ids.Contains(absence.ResourceId)
                        && AbsenceRules.ScheduledStates.Contains(absence.State)
                        && absence.FirstDay <= sunday
                        && absence.LastDay >= monday)
                    .OrderBy(absence => absence.FirstDay).ThenBy(absence => absence.StartTime).ThenBy(absence => absence.Id)
                    .Select(absence => new { absence.Id, absence.ResourceId, absence.Kind, absence.State, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime })
                    .ToListAsync(cancellationToken))
                .Select(absence => (absence.Id, absence.ResourceId, absence.Kind, absence.State, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime))];
        }

        var absencesOf = absences.ToLookup(absence => absence.ResourceId);
        var rows = shown.Select(record => new BoardStaff(
            record.Id,
            record.UserId,
            record.Name,
            [.. days.Select(date => new BoardStaffDay(
                date,
                working.TryGetValue(record.Id, out var theirs) ? [.. theirs.On(date).OrderBy(segment => segment.StartUtc).Select(segment => new BoardBand(segment.LocalStart, segment.LocalEnd))] : [],
                [.. absencesOf[record.Id]
                    .Where(absence => absence.FirstDay <= date && date <= absence.LastDay)
                    .Select(absence => new BoardAbsence(absence.Id, absence.Kind, absence.State, date == absence.FirstDay ? absence.StartTime : null, date == absence.LastDay ? absence.EndTime : null))])
            {
                Closed = working.TryGetValue(record.Id, out var closedFor) ? [.. closedFor.ClosedOn(date).Select(closed => closed.Name)] : [],
            })],
            record.UserId == reader)).ToList();

        return Result<BoardStaffMembers>.Success(new BoardStaffMembers(
            tenantId,
            monday,
            today,
            timeZone.Id,
            filter,
            rows,
            mayAddAbsence,
            ids.Where(held.ContainsKey).ToDictionary(id => id, id => held[id]),
            shownPage,
            filtered.Count));
    }

    private async Task<Result<BoardBookings>> ReadBookingsOfTenantAsync(Guid tenantId, BoardStaffMembers board, CoordinationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var days = board.Days;
        var (monday, sunday) = (days[0], days[^1]);
        var ids = board.StaffMembers.Select(staff => staff.ResourceId).ToList();
        var held = new Dictionary<(Guid Staff, DateOnly Date), IReadOnlyList<BoardOccurrence>>();
        if (ids.Count > 0)
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);

            var bookings = await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId
                    && ids.Contains(assignment.ResourceId)
                    && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned)
                    && assignment.Slot.State == JobSlotState.Active
                    && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress || assignment.Slot.Job.State == JobState.Completed)
                    && assignment.Slot.Job.PeriodStart <= sunday
                    && assignment.Slot.Job.PeriodEnd >= monday)
                .OrderBy(assignment => assignment.Id)
                .Select(assignment => new
                {
                    assignment.Id,
                    assignment.ResourceId,
                    assignment.State,
                    assignment.Slot.JobId,
                    Job = assignment.Slot.Job.Name,
                    Slot = assignment.Slot.Name,
                    Customer = assignment.Slot.Job.Customer.Name,
                    assignment.Slot.Job.PlaceName,
                    assignment.Slot.Job.City,
                    assignment.Slot.Job.Schedule,
                    assignment.Slot.Job.PeriodStart,
                    assignment.Slot.Job.PeriodEnd,
                    assignment.Slot.Job.StartTime,
                    assignment.Slot.Job.EndTime,
                    assignment.LastStaffCommentAt,
                    assignment.LastCoordinatorCommentAt,
                    Occurrences = assignment.Slot.Job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
                })
                .ToListAsync(cancellationToken);

            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(board.TimeZoneId);
            foreach (var group in bookings
                .SelectMany(booking => JobRules.Occurrences(new JobTimes(booking.Schedule, booking.PeriodStart, booking.PeriodEnd, booking.StartTime, booking.EndTime, [.. booking.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))]), monday, sunday, timeZone)
                    .SelectMany(occurrence => JobRules.DaySegments(occurrence).Where(segment => segment.Date >= monday && segment.Date <= sunday).Select(segment => (Occurrence: occurrence, Segment: segment)))
                    .Select(part => (booking.ResourceId, part.Segment.Date, Held: new BoardOccurrence(
                        booking.Id,
                        booking.JobId,
                        booking.Job,
                        booking.Slot,
                        booking.Customer,
                        booking.State,
                        part.Segment.Start,
                        part.Segment.End,
                        StaffConversation.AwaitsCoordinatorReply(booking.LastStaffCommentAt, booking.LastCoordinatorCommentAt),
                        JobPlace.ShortTextOf(booking.PlaceName, booking.City),
                        part.Occurrence.CrossesMidnight ? part.Occurrence.Text : null))))
                .GroupBy(entry => (entry.ResourceId, entry.Date)))
            {
                held[group.Key] = [.. group.Select(entry => entry.Held).OrderBy(held => held.Start).ThenBy(held => held.Job, StringComparer.CurrentCultureIgnoreCase).ThenBy(held => held.AssignmentId)];
            }
        }

        var slots = snapshot.Entries
            .Where(entry => BoardRules.IsToStaff(entry.Row.Kind) && entry.Occurrences.Any(occurrence => occurrence.Date <= sunday && occurrence.EndDate >= monday))
            .ToList();

        var available = new Dictionary<Guid, StaffAvailability>();
        var occurrenceDays = slots.SelectMany(entry => entry.Occurrences).SelectMany(occurrence => new[] { occurrence.Date, occurrence.EndDate }).ToList();
        if (ids.Count > 0 && occurrenceDays.Count > 0)
        {
            foreach (var (from, to) in BoardRules.Windows(occurrenceDays.Min(), occurrenceDays.Max(), StaffAvailabilityService.MaxDays))
            {
                var answer = await this.availability.ReadAsync(ids, from, to, cancellationToken);
                if (!answer.IsSuccess || answer.Value is null)
                    return Result<BoardBookings>.Failure(answer.Error);

                foreach (var (staff, theirs) in answer.Value.StaffMembers)
                {
                    available[staff] = available.TryGetValue(staff, out var before)
                        ? new StaffAvailability(staff, [.. before.Working, .. theirs.Working], [.. before.Blocks, .. theirs.Blocks], [.. before.Warnings, .. theirs.Warnings])
                        : theirs;
                }
            }
        }

        var (pairs, toStaff) = await Task.Run(() => Judge(ids, slots, available, board.Held, monday, sunday), cancellationToken);
        return Result<BoardBookings>.Success(new BoardBookings(monday, board.Filter, held, toStaff, pairs, board.Page));
    }

    private static (Dictionary<(Guid Slot, Guid Staff), BoardPair> Pairs, List<BoardSlotOccurrence> ToStaff) Judge(
        IReadOnlyList<Guid> ids,
        IReadOnlyList<SlotEntry> slots,
        IReadOnlyDictionary<Guid, StaffAvailability> available,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> held,
        DateOnly monday,
        DateOnly sunday)
    {
        var none = (IReadOnlySet<Guid>)new HashSet<Guid>();
        var pairs = new Dictionary<(Guid Slot, Guid Staff), BoardPair>();
        foreach (var slot in slots)
        {
            foreach (var staff in ids)
            {
                pairs[(slot.Row.SlotId, staff)] = BoardRules.Pair(
                    available.TryGetValue(staff, out var theirs) ? theirs : new StaffAvailability(staff, [], [], []),
                    slot.Occurrences,
                    slot.Required,
                    held.TryGetValue(staff, out var qualified) ? qualified : none,
                    slot.LiveStaffMembers.Contains(staff));
            }
        }

        var toStaff = slots
            .SelectMany(slot =>
            {
                var judged = ids.Select(staff => pairs[(slot.Row.SlotId, staff)]).Where(pair => !pair.Asked).ToList();
                var free = judged.Count(pair => pair.IsFree);
                return slot.Occurrences
                    .SelectMany(occurrence => JobRules.DaySegments(occurrence).Select(segment => (Occurrence: occurrence, Segment: segment)))
                    .Where(part => part.Segment.Date >= monday && part.Segment.Date <= sunday)
                    .Select(part => new BoardSlotOccurrence(slot.Row.SlotId, slot.Row.Slot, slot.Row.JobId, slot.Row.Job, slot.Row.Customer, slot.Row.Kind, part.Segment.Date, part.Segment.Start, part.Segment.End, free, judged.Count, slot.Place, part.Occurrence.CrossesMidnight ? part.Occurrence.Text : null));
            })
            .OrderBy(occurrence => occurrence.Date)
            .ThenBy(occurrence => occurrence.Start)
            .ThenBy(occurrence => occurrence.Job, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(occurrence => occurrence.Slot, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(occurrence => occurrence.SlotId)
            .ToList();
        return (pairs, toStaff);
    }
}
