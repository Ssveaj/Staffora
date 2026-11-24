using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Settings;

namespace Staffora.Services.Scheduling;

public sealed class StaffAvailabilityService : IStaffAvailability
{
    public const int MaxDays = 731;

    public const string NoViewCoordination = "You do not have permission to view the coordination.";

    public const string NoScheduleView = "You do not have permission to view this staff member's schedule.";

    public const string NoOwnSchedule = "You do not have permission to view your own schedule.";

    public const string RangeReversed = "The range ends before it starts.";

    public static readonly string RangeTooLong = $"Availability is read for at most {MaxDays} days at a time.";

    public const string RangeOutOfCalendar = "The range lies outside the calendar.";

    public static readonly string WeeksOutOfRange = $"A schedule is read for 1 to {MaxDays / 7} weeks.";

    public const string ReadFailed = "The staff members' schedules could not be read. Try again.";

    public const string StaffNotFound = "Staff member not found.";

    private static readonly RolePermission[] scheduleReaders = [RolePermission.ResourcesView, RolePermission.ViewCoordination];

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly TenantClock clock;
    private readonly ILogger<StaffAvailabilityService> log;

    public StaffAvailabilityService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, TenantClock clock, ILogger<StaffAvailabilityService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.clock = clock;
        this.log = log;
    }

    public async Task<Result<AvailabilityAnswer>> ReadAsync(IReadOnlyCollection<Guid> resourceIds, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<AvailabilityAnswer>.Failure(NoViewCoordination);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<AvailabilityAnswer>.Failure(IRuntimeExecutionContext.NoTenant);

        if (RangeProblem(from, to) is { } problem)
            return Result<AvailabilityAnswer>.Failure(problem);

        var zone = await this.clock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<AvailabilityAnswer>.Failure(zone.Error);

        var holidays = await this.clock.GetHolidaysAsync(cancellationToken);
        if (!holidays.IsSuccess || holidays.Value is not { } tenantHolidays)
            return Result<AvailabilityAnswer>.Failure(holidays.Error);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            return Result<AvailabilityAnswer>.Success(await ComputeAsync(context, tenantId, resourceIds, from, to, timeZone, tenantHolidays, cancellationToken));
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "the staff members' availability", cancellationToken);
            this.log.LogError(exception, "Reading the availability of {Count} staff members of tenant {TenantId} failed.", resourceIds.Count, tenantId);
            return Result<AvailabilityAnswer>.Failure(ReadFailed);
        }
    }

    public async Task<Result<StaffSchedule>> ReadScheduleOfUserAsync(Guid userId, DateOnly from, int weeks, CancellationToken cancellationToken = default)
    {
        var session = this.runtime.Current;
        var mayRead = await this.permissionService.HasAnyPermissionAsync(scheduleReaders, cancellationToken)
            || (session.EffectiveUserId == userId && await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnSchedule, cancellationToken));
        if (!mayRead)
            return Result<StaffSchedule>.Failure(NoScheduleView);

        if (session.TenantId is not { } tenantId)
            return Result<StaffSchedule>.Failure(IRuntimeExecutionContext.NoTenant);

        if (weeks < 1 || weeks > MaxDays / 7)
            return Result<StaffSchedule>.Failure(WeeksOutOfRange);

        if (from <= DateOnly.MinValue.AddDays(7) || from >= DateOnly.MaxValue.AddDays(-MaxDays - 7))
            return Result<StaffSchedule>.Failure(RangeOutOfCalendar);

        var monday = ScheduleRules.MondayOf(from);
        var last = StaffSchedule.LastDayOf(monday, weeks);
        if (RangeProblem(monday, last) is { } problem)
            return Result<StaffSchedule>.Failure(problem);

        var zone = await this.clock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<StaffSchedule>.Failure(zone.Error);

        var holidays = await this.clock.GetHolidaysAsync(cancellationToken);
        if (!holidays.IsSuccess || holidays.Value is not { } tenantHolidays)
            return Result<StaffSchedule>.Failure(holidays.Error);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var resourceId = await context.Resources.AsNoTracking()
                .Where(resource => resource.TenantId == tenantId && resource.UserId == userId)
                .Select(resource => (Guid?)resource.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (resourceId is not { } staff)
                return Result<StaffSchedule>.Failure(StaffNotFound);

            var answer = await ComputeAsync(context, tenantId, [staff], monday, last, timeZone, tenantHolidays, cancellationToken);
            return Result<StaffSchedule>.Success(StaffSchedule.ByWeek(answer.StaffMembers[staff], monday, weeks, timeZone.Id));
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "a staff member's schedule", cancellationToken);
            this.log.LogError(exception, "Reading the schedule of user {UserId} of tenant {TenantId} failed.", userId, tenantId);
            return Result<StaffSchedule>.Failure(ReadFailed);
        }
    }

    public async Task<Result<OwnScheduleWeek>> ReadOwnWeekAsync(DateOnly? day, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnSchedule, cancellationToken))
            return Result<OwnScheduleWeek>.Failure(NoOwnSchedule);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<OwnScheduleWeek>.Failure(IRuntimeExecutionContext.NoTenant);

        if (day is { } asked && !ScheduleRules.HasReadableWeek(asked))
            return Result<OwnScheduleWeek>.Failure(RangeOutOfCalendar);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<OwnScheduleWeek>.Failure(today.Error);

        var zone = await this.clock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<OwnScheduleWeek>.Failure(zone.Error);

        var holidays = await this.clock.GetHolidaysAsync(cancellationToken);
        if (!holidays.IsSuccess || holidays.Value is not { } tenantHolidays)
            return Result<OwnScheduleWeek>.Failure(holidays.Error);

        var monday = ScheduleRules.MondayOf(day ?? today.Value);
        var sunday = monday.AddDays(WorkPatternRules.DaysPerWeek - 1);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var resourceId = await context.Resources.AsNoTracking()
                .Where(resource => resource.TenantId == tenantId && resource.UserId == self)
                .Select(resource => (Guid?)resource.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (resourceId is not { } staff)
                return Result<OwnScheduleWeek>.Failure(StaffNotFound);

            var read = await ReadStaffDaysAsync(context, tenantId, staff, monday, sunday, timeZone, tenantHolidays, liveBookingStates, cancellationToken);
            return Result<OwnScheduleWeek>.Success(new OwnScheduleWeek(monday, today.Value, timeZone.Id, read.Days));
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "the own week", cancellationToken);
            this.log.LogError(exception, "Reading the own week of user {UserId} of tenant {TenantId} failed.", self, tenantId);
            return Result<OwnScheduleWeek>.Failure(ReadFailed);
        }
    }

    public async Task<Result<OwnScheduleMonth>> ReadMonthAsync(Guid? resourceId, DateOnly? month, CancellationToken cancellationToken = default)
    {
        var coordinates = await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken);
        var readsOwn = await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnSchedule, cancellationToken);
        if (resourceId is null && !readsOwn)
            return Result<OwnScheduleMonth>.Failure(NoOwnSchedule);

        if (resourceId is not null && !coordinates && !readsOwn)
            return Result<OwnScheduleMonth>.Failure(NoScheduleView);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<OwnScheduleMonth>.Failure(IRuntimeExecutionContext.NoTenant);

        if (month is { } asked && !OwnScheduleMonth.IsReadable(OwnScheduleMonth.FirstOf(asked)))
            return Result<OwnScheduleMonth>.Failure(RangeOutOfCalendar);

        var today = await this.clock.TodayAsync(cancellationToken);
        if (!today.IsSuccess)
            return Result<OwnScheduleMonth>.Failure(today.Error);

        var zone = await this.clock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<OwnScheduleMonth>.Failure(zone.Error);

        var holidays = await this.clock.GetHolidaysAsync(cancellationToken);
        if (!holidays.IsSuccess || holidays.Value is not { } tenantHolidays)
            return Result<OwnScheduleMonth>.Failure(holidays.Error);

        var first = OwnScheduleMonth.FirstOf(month ?? today.Value);
        var last = first.AddMonths(1).AddDays(-1);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);

            var records = context.Resources.AsNoTracking().Where(resource => resource.TenantId == tenantId);
            records = resourceId switch
            {
                null => records.Where(resource => resource.UserId == self),
                { } named when coordinates => records.Where(resource => resource.Id == named),
                { } named => records.Where(resource => resource.Id == named && resource.UserId == self),
            };
            var record = await records.Select(resource => new { resource.Id, resource.UserId }).FirstOrDefaultAsync(cancellationToken);
            if (record is null)
                return Result<OwnScheduleMonth>.Failure(resourceId is not null && !coordinates ? NoScheduleView : StaffNotFound);

            var staff = record.Id;
            var read = await ReadStaffDaysAsync(context, tenantId, staff, first, last, timeZone, tenantHolidays, assignedStates, cancellationToken);

            var patterns = (await context.ResourcePatternAssignments.AsNoTracking()
                    .Where(assignment => assignment.TenantId == tenantId
                        && assignment.ResourceId == staff
                        && (assignment.State == ResourcePatternAssignmentState.Active || assignment.State == ResourcePatternAssignmentState.Registration))
                    .Select(assignment => new
                    {
                        assignment.Id,
                        assignment.State,
                        assignment.EffectiveFrom,
                        assignment.EffectiveTo,
                        assignment.CycleAnchor,
                        assignment.WorkPattern.Name,
                        assignment.WorkPattern.CycleWeeks,
                        Segments = assignment.WorkPattern.Segments.Select(segment => new { segment.DayIndex, segment.Start, segment.End }).ToList(),
                    })
                    .ToListAsync(cancellationToken))
                .Select(assignment => new OwnPatternPeriod(assignment.Id, assignment.Name, assignment.EffectiveFrom, assignment.EffectiveTo, assignment.CycleWeeks, assignment.CycleAnchor, assignment.State == ResourcePatternAssignmentState.Active,
                    [.. assignment.Segments.Select(segment => new PatternSegment(segment.DayIndex, segment.Start, segment.End))]))
                .OrderBy(pattern => pattern.From).ThenBy(pattern => pattern.AssignmentId)
                .ToList();
            return Result<OwnScheduleMonth>.Success(BuildMonth(staff, record.UserId, first, last, today.Value, timeZone.Id, read, patterns));
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "a month of a staff member's schedule", cancellationToken);
            this.log.LogError(exception, "Reading a month of the schedule of staff record {ResourceId} (user {UserId}) of tenant {TenantId} failed.", resourceId, self, tenantId);
            return Result<OwnScheduleMonth>.Failure(ReadFailed);
        }
    }

    private static readonly SlotAssignmentState[] assignedStates = [SlotAssignmentState.Assigned];

    private static readonly SlotAssignmentState[] liveBookingStates = [SlotAssignmentState.Requested, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned];

    private sealed record StaffAbsenceFact(Guid Id, AbsenceKind Kind, ResourceAbsenceState State, DateOnly FirstDay, TimeOnly? StartTime, DateOnly LastDay, TimeOnly? EndTime);

    private sealed record StaffBookingFact(Guid Id, string Job, bool Recurring, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<DateOnly> Dates);

    private sealed record StaffDaysRead(IReadOnlyList<OwnScheduleDay> Days, IReadOnlyList<StaffAbsenceFact> Absences, IReadOnlyList<StaffBookingFact> Bookings);

    private static async Task<StaffDaysRead> ReadStaffDaysAsync(ApplicationDbContext context, Guid tenantId, Guid staff, DateOnly from, DateOnly to, TimeZoneInfo timeZone, TenantHolidays holidays, SlotAssignmentState[] states, CancellationToken cancellationToken)
    {
        var answer = await ComputeAsync(context, tenantId, [staff], from, to, timeZone, holidays, cancellationToken);
        var working = answer.StaffMembers.TryGetValue(staff, out var own) ? own.Working : [];

        var absences = await context.ResourceAbsences.AsNoTracking()
            .Where(absence => absence.TenantId == tenantId
                && absence.ResourceId == staff
                && AbsenceRules.ScheduledStates.Contains(absence.State)
                && absence.FirstDay <= to
                && absence.LastDay >= from)
            .OrderBy(absence => absence.FirstDay).ThenBy(absence => absence.StartTime).ThenBy(absence => absence.Id)
            .Select(absence => new StaffAbsenceFact(absence.Id, absence.Kind, absence.State, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime))
            .ToListAsync(cancellationToken);

        var bookings = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId
                && assignment.ResourceId == staff
                && states.Contains(assignment.State)
                && assignment.Slot.State == JobSlotState.Active
                && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress || assignment.Slot.Job.State == JobState.Completed)
                && assignment.Slot.Job.PeriodStart <= to
                && assignment.Slot.Job.PeriodEnd >= from)
            .Select(assignment => new
            {
                assignment.Id,
                assignment.State,
                Job = assignment.Slot.Job.Name,
                Slot = assignment.Slot.Name,
                Customer = assignment.Slot.Job.Customer.Name,
                assignment.Slot.Job.Schedule,
                assignment.Slot.Job.PeriodStart,
                assignment.Slot.Job.PeriodEnd,
                assignment.Slot.Job.StartTime,
                assignment.Slot.Job.EndTime,
                assignment.Slot.Job.PlaceName,
                assignment.Slot.Job.City,
                Occurrences = assignment.Slot.Job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var occurring = bookings
            .Select(booking => (Booking: booking, Occurrences: JobRules.Occurrences(new JobTimes(booking.Schedule, booking.PeriodStart, booking.PeriodEnd, booking.StartTime, booking.EndTime, [.. booking.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))]), from, to, timeZone)))
            .ToList();
        var booked = occurring
            .SelectMany(entry => entry.Occurrences
                .SelectMany(JobRules.DaySegments)
                .Select(segment => (segment.Date, Entry: new OwnOccurrenceEntry(entry.Booking.Id, entry.Booking.Job, entry.Booking.Slot, entry.Booking.State, segment.Start, segment.End)
                {
                    Place = JobPlace.ShortTextOf(entry.Booking.PlaceName, entry.Booking.City),
                    Customer = entry.Booking.Customer,
                })))
            .ToLookup(entry => entry.Date, entry => entry.Entry);

        var holidaysOn = holidays.ByDay(from, to);
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(offset => from.AddDays(offset)).Select(date => new OwnScheduleDay(
            date,
            [.. working.Where(segment => segment.Date == date).OrderBy(segment => segment.StartUtc)],
            [.. absences.Where(absence => absence.FirstDay <= date && date <= absence.LastDay).Select(absence => new OwnAbsenceEntry(
                absence.Id,
                absence.Kind,
                absence.State,
                date == absence.FirstDay ? absence.StartTime : null,
                date == absence.LastDay ? absence.EndTime : null))],
            [.. booked[date]
                .OrderBy(occurrence => occurrence.Start).ThenBy(occurrence => occurrence.Job, StringComparer.CurrentCulture).ThenBy(occurrence => occurrence.AssignmentId)])
            {
                Holidays = holidaysOn.TryGetValue(date, out var holidaysOfDay) ? [.. holidaysOfDay.Select(holidays.TextOf)] : [],
            })
            .ToList();
        var facts = occurring
            .Select(entry => new StaffBookingFact(entry.Booking.Id, entry.Booking.Job, entry.Booking.Schedule == JobSchedule.Recurring, entry.Booking.PeriodStart, entry.Booking.PeriodEnd, [.. entry.Occurrences.Select(occurrence => occurrence.Date)]))
            .ToList();
        return new StaffDaysRead(days, absences, facts);
    }

    private static OwnScheduleMonth BuildMonth(Guid resourceId, Guid userId, DateOnly first, DateOnly last, DateOnly today, string timeZoneId, StaffDaysRead read, IReadOnlyList<OwnPatternPeriod> patterns)
    {
        var active = patterns.Where(pattern => pattern.Confirmed).ToList();
        var dates = active.Select(pattern => new ScheduledDates(pattern.AssignmentId, pattern.From, pattern.To)).ToList();
        OwnPatternPeriod? HoldingOn(DateOnly date) =>
            ScheduleRules.AssignmentOn(date, dates) is { } holding ? active.First(pattern => pattern.AssignmentId == holding.AssignmentId) : null;

        var reference = first <= today && today <= last ? today : first;
        var pattern = HoldingOn(reference) ?? active.FirstOrDefault(candidate => candidate.From > reference && candidate.From <= last);
        var next = patterns.Where(candidate => candidate.AssignmentId != pattern?.AssignmentId && candidate.From > (pattern?.From ?? reference)).MinBy(candidate => candidate.From);
        var previous = pattern is null ? null : active.Where(candidate => candidate.To is { } to && to < pattern.From).MaxBy(candidate => candidate.To);

        var notes = new Dictionary<DateOnly, List<string>>();
        void Note(DateOnly date, string note)
        {
            if (date >= first && date <= last)
            {
                (notes.TryGetValue(date, out var day) ? day : notes[date] = []).Add(note);
            }
        }

        foreach (var period in patterns)
        {
            Note(period.From, period.Confirmed ? $"Work pattern {period.Name} starts" : $"Work pattern {period.Name} starts, not confirmed yet");
            if (period.Confirmed && period.To is { } to)
            {
                Note(to, $"Work pattern {period.Name} ends");
            }
        }

        for (var date = ScheduleRules.MondayOf(first); date <= last; date = date.AddDays(WorkPatternRules.DaysPerWeek))
        {
            if (HoldingOn(date) is { CycleWeeks: > 1 } rotating && rotating.Covers(date.AddDays(-1)))
            {
                Note(date, $"Pattern week {rotating.CycleWeekOn(date)} starts");
            }
        }

        foreach (var booking in read.Bookings.Where(booking => booking.Recurring && booking.PeriodEnd <= last && booking.Dates.Count > 0))
        {
            var lastTime = booking.Dates.Max();
            if (lastTime > booking.PeriodStart)
            {
                Note(lastTime, $"Last time on {booking.Job}");
            }
        }

        foreach (var absence in read.Absences)
        {
            var kind = absence.Kind == AbsenceKind.SickLeave ? "Sick leave" : "Planned leave";
            Note(absence.FirstDay, absence.LastDay > absence.FirstDay ? $"{kind} starts, until {OwnScheduleMonth.ShortDate(absence.LastDay)}" : $"{kind} starts");
        }

        var weeks = read.Days
            .Select(day => notes.TryGetValue(day.Date, out var said) ? day with { Notes = said } : day)
            .GroupBy(day => ScheduleRules.MondayOf(day.Date))
            .Select(week => new OwnMonthWeek(week.Key, [.. week])
            {
                PatternWeek = HoldingOn(week.First().Date) is { CycleWeeks: > 1 } rotating ? rotating.CycleWeekOn(week.First().Date) : null,
            })
            .ToList();
        return new OwnScheduleMonth(first, today, timeZoneId, weeks)
        {
            ResourceId = resourceId,
            UserId = userId,
            Pattern = pattern,
            NextPattern = next,
            PreviousPattern = previous,
        };
    }

    private static string? RangeProblem(DateOnly from, DateOnly to) =>
        from == DateOnly.MinValue || to >= DateOnly.MaxValue ? RangeOutOfCalendar
        : to < from ? RangeReversed
        : to.DayNumber - from.DayNumber + 1 > MaxDays ? RangeTooLong
        : null;

    internal static async Task<AvailabilityAnswer> ComputeAsync(ApplicationDbContext context, Guid tenantId, IReadOnlyCollection<Guid> resourceIds, DateOnly from, DateOnly to, TimeZoneInfo zone, TenantHolidays holidays, CancellationToken cancellationToken)
    {
        var asked = resourceIds.Distinct().ToList();
        if (asked.Count == 0)
        {
            return new AvailabilityAnswer(from, to, zone.Id, new Dictionary<Guid, StaffAvailability>());
        }

        var records = (await context.Resources.AsNoTracking()
            .Where(resource => resource.TenantId == tenantId && asked.Contains(resource.Id))
            .Select(resource => new { resource.Id, resource.State, UserState = resource.User.State })
            .ToListAsync(cancellationToken))
            .Select(record => new ScheduledStaff(record.Id, StaffServiceRules.IsInService(record.State, record.UserState)))
            .ToList();
        var staffMembers = records.Select(record => record.ResourceId).ToList();

        var assignments = await context.ResourcePatternAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId
                && staffMembers.Contains(assignment.ResourceId)
                && assignment.State == ResourcePatternAssignmentState.Active
                && assignment.EffectiveFrom <= to
                && (assignment.EffectiveTo == null || assignment.EffectiveTo >= from))
            .Select(assignment => new ScheduledAssignment(assignment.Id, assignment.ResourceId, assignment.WorkPatternId, assignment.EffectiveFrom, assignment.EffectiveTo, assignment.CycleAnchor))
            .ToListAsync(cancellationToken);

        var patternIds = assignments.Select(assignment => assignment.WorkPatternId).Distinct().ToList();
        var patterns = patternIds.Count == 0
            ? new Dictionary<Guid, PatternShape>()
            : (await context.WorkPatterns.AsNoTracking()
                    .Where(pattern => pattern.TenantId == tenantId && patternIds.Contains(pattern.Id))
                    .Select(pattern => new
                    {
                        pattern.Id,
                        pattern.CycleWeeks,
                        Segments = pattern.Segments.Select(segment => new { segment.DayIndex, segment.Start, segment.End }).ToList(),
                    })
                    .ToListAsync(cancellationToken))
                .ToDictionary(pattern => pattern.Id, pattern => new PatternShape(pattern.Id, pattern.CycleWeeks, [.. pattern.Segments.Select(segment => new PatternSegment(segment.DayIndex, segment.Start, segment.End))]));

        var absences = staffMembers.Count == 0
            ? []
            : await context.ResourceAbsences.AsNoTracking()
                .Where(absence => absence.TenantId == tenantId
                    && staffMembers.Contains(absence.ResourceId)
                    && (absence.State == ResourceAbsenceState.Approved || (AbsenceRules.UndecidedStates.Contains(absence.State) && absence.Kind == AbsenceKind.SickLeave))
                    && absence.FirstDay <= to
                    && absence.LastDay >= from)
                .Select(absence => new ScheduledAbsence(absence.Id, absence.ResourceId, absence.Kind, absence.State, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime))
                .ToListAsync(cancellationToken);

        var bookings = staffMembers.Count == 0
            ? []
            : (await context.SlotAssignments.AsNoTracking()
                    .Where(assignment => assignment.TenantId == tenantId
                        && staffMembers.Contains(assignment.ResourceId)
                        && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned)
                        && assignment.Slot.State == JobSlotState.Active
                        && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress)
                        && assignment.Slot.Job.PeriodStart <= to
                        && assignment.Slot.Job.PeriodEnd >= from)
                    .Select(assignment => new
                    {
                        assignment.Id,
                        assignment.ResourceId,
                        assignment.State,
                        assignment.Slot.Job.Schedule,
                        assignment.Slot.Job.PeriodStart,
                        assignment.Slot.Job.PeriodEnd,
                        assignment.Slot.Job.StartTime,
                        assignment.Slot.Job.EndTime,
                        Occurrences = assignment.Slot.Job.TimeSlots.Select(timeSlot => new { timeSlot.Weekday, timeSlot.Start, timeSlot.End }).ToList(),
                    })
                    .ToListAsync(cancellationToken))
                .Select(booking => new ScheduledBooking(booking.Id, booking.ResourceId, booking.State, new JobTimes(booking.Schedule, booking.PeriodStart, booking.PeriodEnd, booking.StartTime, booking.EndTime, [.. booking.Occurrences.Select(occurrence => new OccurrenceTimes(occurrence.Weekday, occurrence.Start, occurrence.End))])))
                .ToList();

        var (staffed, closedDays) = await ClosedDaysOfAsync(context, tenantId, records, from, to, cancellationToken);
        return new AvailabilityAnswer(from, to, zone.Id, AvailabilityRules.Compute(staffed, assignments, patterns, absences, bookings, [.. closedDays, .. holidays.ClosedDays(from, to)], from, to, zone));
    }

    private static async Task<(IReadOnlyList<ScheduledStaff> Staff, IReadOnlyList<ScheduledClosedDay> ClosedDays)> ClosedDaysOfAsync(ApplicationDbContext context, Guid tenantId, List<ScheduledStaff> records, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return (records, []);
        }

        var closed = await context.ClosedDays.AsNoTracking()
            .Where(closedDay => closedDay.TenantId == tenantId && closedDay.FirstDay <= to && closedDay.LastDay >= from)
            .OrderBy(closedDay => closedDay.FirstDay).ThenBy(closedDay => closedDay.Id)
            .Select(closedDay => new { closedDay.Id, closedDay.Name, closedDay.FirstDay, closedDay.LastDay, closedDay.OrganizationUnitId })
            .ToListAsync(cancellationToken);
        if (closed.All(closedDay => closedDay.OrganizationUnitId is null))
        {
            return (records, [.. closed.Select(closedDay => new ScheduledClosedDay(closedDay.Id, closedDay.Name, closedDay.FirstDay, closedDay.LastDay, null))]);
        }

        var staffMembers = records.Select(record => record.ResourceId).ToList();
        var memberships = (await context.ResourceUnitMemberships.AsNoTracking()
                .Where(membership => membership.TenantId == tenantId && staffMembers.Contains(membership.ResourceId))
                .Select(membership => new { membership.ResourceId, membership.OrganizationUnitId })
                .ToListAsync(cancellationToken))
            .ToLookup(membership => membership.ResourceId, membership => membership.OrganizationUnitId);
        var units = await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.TenantId == tenantId)
            .Select(unit => new { unit.Id, unit.TenantId, unit.Name, unit.UnitType, unit.ParentId })
            .ToListAsync(cancellationToken);
        var structure = OrganizationStructure.Of(units.Select(unit => (unit.Id, unit.TenantId, unit.Name, unit.UnitType, unit.ParentId)));
        return (
            [.. records.Select(record => record with { Units = memberships[record.ResourceId].ToHashSet() })],
            [.. closed.Select(closedDay => new ScheduledClosedDay(closedDay.Id, closedDay.Name, closedDay.FirstDay, closedDay.LastDay, closedDay.OrganizationUnitId is { } unit ? structure.SelfAndDescendants(unit) : null))]);
    }
}
