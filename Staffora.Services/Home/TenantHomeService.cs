using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using Staffora.Services.Settings;

namespace Staffora.Services.Home;

public sealed partial class TenantHomeService
{
    public const string NoView = "You do not have permission to view the administration overview.";

    public const string NoCoordination = "You do not have permission to view the staffing overview.";

    public const string NoTenant = "A home is read in a tenant of one's own.";

    public const string ReadFailed = "The overview could not be read. Try again.";

    public const string SystemActor = "System";

    private static readonly JobState[] WeekJobStates = [JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress, JobState.Completed];

    private static readonly JobState[] LiveJobStates = [JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress];

    private static readonly JobState[] StaffedJobStates = [JobState.Coordinating, JobState.Confirmed, JobState.InProgress];

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly TenantClock tenantClock;
    private readonly TimeProvider clock;
    private readonly ILogger<TenantHomeService> log;

    public TenantHomeService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, TenantClock tenantClock, TimeProvider clock, ILogger<TenantHomeService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.tenantClock = tenantClock;
        this.clock = clock;
        this.log = log;
    }

    public async Task<Result<AdministrationHome>> ReadAdministrationAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.TenantView, cancellationToken))
            return Result<AdministrationHome>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.HasPlatformReach() || session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<AdministrationHome>.Failure(NoTenant);

        var held = await this.permissionService.GetUserPermissionsAsync(cancellationToken);
        bool Holds(IEnumerable<RolePermission> any) => any.Any(held.Contains);
        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<AdministrationHome>.Failure(zone.Error ?? ReadFailed);

        var instant = this.clock.GetUtcNow();
        var now = instant.UtcDateTime;
        var today = JobRules.LocalDay(instant, timeZone);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);

            var firstName = await FirstNameAsync(context, tenantId, self, cancellationToken);
            var home = new AdministrationHome(firstName, now, today);

            var readsJobs = Holds(JobService.Readers);
            if (readsJobs)
            {
                home = home with
                {
                    Staffing = await StaffingAsync(context, tenantId, today, now, timeZone, cancellationToken),
                    Activity = await ActivityAsync(context, tenantId, cancellationToken),
                };
            }

            if (Holds([RolePermission.ResourcesView]))
            {
                var inService = await context.Resources.AsNoTracking()
                    .Where(resource => resource.TenantId == tenantId)
                    .Where(StaffServiceRules.InService)
                    .CountAsync(cancellationToken);

                var withoutPattern = await WithoutPatternAsync(context, tenantId, today, cancellationToken);
                home = home with { People = new HomePeople(inService, withoutPattern) };
            }

            if (Holds(ResourceAbsenceService.Readers))
            {
                home = home with { Absence = await AbsenceAsync(context, tenantId, today, cancellationToken) };
            }

            if (Holds([RolePermission.CustomersView, RolePermission.CustomersManage]))
            {
                home = home with { Customers = await CustomersAsync(context, tenantId, readsJobs, cancellationToken) };
            }

            return Result<AdministrationHome>.Success(home);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the administration home of tenant {TenantId} failed.", tenantId);
            return Result<AdministrationHome>.Failure(ReadFailed);
        }
    }

    public async Task<Result<CoordinatorHome>> ReadCoordinatorAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<CoordinatorHome>.Failure(NoCoordination);

        var session = this.runtime.Current;
        if (session.HasPlatformReach() || session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<CoordinatorHome>.Failure(NoTenant);

        var approves = await this.permissionService.HasPermissionAsync(RolePermission.ApproveResourceAbsence, cancellationToken);
        var readsStaff = await this.permissionService.HasPermissionAsync(RolePermission.ResourcesView, cancellationToken);
        var zone = await this.tenantClock.GetTimeZoneAsync(cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } timeZone)
            return Result<CoordinatorHome>.Failure(zone.Error ?? ReadFailed);

        var instant = this.clock.GetUtcNow();
        var now = instant.UtcDateTime;
        var today = JobRules.LocalDay(instant, timeZone);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var firstName = await FirstNameAsync(context, tenantId, self, cancellationToken);
            var tenantName = await context.Tenants.AsNoTracking()
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
            var staffing = await StaffingAsync(context, tenantId, today, now, timeZone, cancellationToken);
            var answers = await AnswersAsync(context, tenantId, today, cancellationToken);
            var todayJobs = await TodayJobsAsync(context, tenantId, today, timeZone, cancellationToken);
            var home = new CoordinatorHome(firstName, tenantName, now, today, staffing, answers, todayJobs);

            if (approves)
            {
                var waiting = WaitingAbsences(context, tenantId, today);
                var count = await waiting.CountAsync(cancellationToken);
                home = home with { Approvals = new HomeApprovals(count == 0 ? [] : await AbsenceRows(waiting).ToListAsync(cancellationToken), count) };
            }

            var portals = await context.Customers.AsNoTracking()
                .CountAsync(customer => customer.TenantId == tenantId && customer.State == CustomerState.Active && customer.LinkedTenantId != null, cancellationToken);
            if (portals > 0)
            {
                home = home with { Portal = await PortalAsync(context, tenantId, cancellationToken) };
            }

            if (readsStaff)
            {
                home = home with { WithoutWorkPattern = await WithoutPatternAsync(context, tenantId, today, cancellationToken) };
            }

            return Result<CoordinatorHome>.Success(home);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the coordinator's home of tenant {TenantId} failed.", tenantId);
            return Result<CoordinatorHome>.Failure(ReadFailed);
        }
    }

    private static async Task<HomeAnswers> AnswersAsync(ApplicationDbContext context, Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        var live = context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId
                && assignment.Slot.State == JobSlotState.Active
                && StaffedJobStates.Contains(assignment.Slot.Job.State)
                && assignment.Slot.Job.PeriodEnd >= today);
        var counts = await live
            .Where(assignment => assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted)
            .GroupBy(assignment => assignment.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var unanswered = counts.Where(count => SlotAssignmentWorkflowAttribute.IsAsked(count.State)).Sum(count => count.Count);
        var yesCount = counts.Where(count => count.State == SlotAssignmentState.Accepted).Sum(count => count.Count);

        var yeses = yesCount == 0 ? [] : await live
            .Where(assignment => assignment.State == SlotAssignmentState.Accepted)
            .OrderBy(assignment => assignment.Slot.Job.PeriodStart)
            .ThenBy(assignment => assignment.Slot.Job.Name)
            .ThenBy(assignment => assignment.Id)
            .Take(HomeRules.Shown)
            .Select(assignment => new HomeYes(
                assignment.Id,
                assignment.Slot.JobId,
                assignment.Slot.Job.Name,
                assignment.Slot.Name,
                (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(),
                assignment.Slot.Job.Customer.Name,
                assignment.Slot.Job.PeriodStart))
            .ToListAsync(cancellationToken);
        return new HomeAnswers(unanswered, yeses, yesCount);
    }

    private static async Task<IReadOnlyList<HomeTodayJob>> TodayJobsAsync(ApplicationDbContext context, Guid tenantId, DateOnly today, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var yesterday = today.AddDays(-1);
        var (day, dayBefore) = (WeekdayExtensions.WeekdayOf(today), WeekdayExtensions.WeekdayOf(yesterday));
        var jobs = await context.Jobs.AsNoTracking()
            .AsSplitQuery()
            .Where(job => job.TenantId == tenantId
                && WeekJobStates.Contains(job.State)
                && job.PeriodStart <= today
                && job.PeriodEnd >= yesterday
                && (job.Schedule == JobSchedule.Single || job.TimeSlots.Any(time => time.Weekday == day || time.Weekday == dayBefore)))
            .Select(job => new
            {
                job.Id,
                job.Name,
                Customer = job.Customer.Name,
                job.State,
                job.PlaceName,
                job.City,
                job.Schedule,
                job.PeriodStart,
                job.PeriodEnd,
                job.StartTime,
                job.EndTime,
                Weekly = job.TimeSlots.Select(time => new { time.Weekday, time.Start, time.End }).ToList(),
                Slots = job.Slots.Count(slot => slot.State == JobSlotState.Active),
                Held = job.Slots.Count(slot => slot.State == JobSlotState.Active && slot.Assignments.Any(assignment => assignment.State == SlotAssignmentState.Assigned)),
            })
            .ToListAsync(cancellationToken);

        return [.. jobs
            .Select(job => new
            {
                Job = job,
                Times = JobRules.Occurrences(new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, [.. job.Weekly.Select(time => new OccurrenceTimes(time.Weekday, time.Start, time.End))]), today, today, zone),
            })
            .Where(found => found.Times.Count > 0)
            .OrderBy(found => found.Times[0].StartUtc)
            .ThenBy(found => found.Job.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(found => found.Job.Id)
            .Select(found => new HomeTodayJob(found.Job.Id, found.Job.Name, found.Job.Customer, found.Job.State, JobPlace.ShortTextOf(found.Job.PlaceName, found.Job.City), found.Times, found.Job.Slots, found.Job.Held))];
    }

    private static async Task<HomePortal> PortalAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var sent = context.Jobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.LinkedCrossTenantId != null && (job.State == JobState.AwaitingCoordination || job.State == JobState.Coordinating));
        var counts = await sent
            .GroupBy(job => job.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var orders = new HomeOrders(
            counts.Where(count => count.State == JobState.AwaitingCoordination).Sum(count => count.Count),
            counts.Where(count => count.State == JobState.Coordinating).Sum(count => count.Count));
        var orderRows = orders.Waiting + orders.BeingStaffed == 0 ? [] : await sent
            .OrderBy(job => job.State == JobState.AwaitingCoordination ? 0 : 1)
            .ThenBy(job => job.PeriodStart)
            .ThenBy(job => job.Name)
            .ThenBy(job => job.Id)
            .Take(HomeRules.Shown)
            .Select(job => new HomePortalOrder(job.Id, job.Name, job.Customer.Name, job.State, job.PeriodStart))
            .ToListAsync(cancellationToken);

        var awaiting = context.CommentContainers.AsNoTracking()
            .Where(thread => thread.TenantId == tenantId
                && thread.EntityType == nameof(Job)
                && thread.Audience == CommentAudience.Customer
                && thread.Comments
                    .OrderByDescending(comment => comment.Created)
                    .ThenByDescending(comment => comment.Id)
                    .Select(comment => comment.Kind == CommentKind.Comment && comment.AuthorType == CommentAuthorType.Contact)
                    .FirstOrDefault())
            .Join(
                context.Jobs.Where(job => job.TenantId == tenantId && LiveJobStates.Contains(job.State)),
                thread => thread.EntityId,
                job => job.Id,
                (thread, job) => new { job.Id, job.Name, Customer = job.Customer.Name, At = thread.Comments.Max(comment => comment.Created) });
        var messageCount = await awaiting.CountAsync(cancellationToken);
        var messages = messageCount == 0 ? [] : await awaiting
            .OrderBy(row => row.At)
            .ThenBy(row => row.Id)
            .Take(HomeRules.Shown)
            .Select(row => new HomeCustomerMessage(row.Id, row.Name, row.Customer, row.At))
            .ToListAsync(cancellationToken);
        return new HomePortal(orders, orderRows, messages, messageCount);
    }

    public async Task<Result<string?>> ReadFirstNameAsync(CancellationToken cancellationToken = default)
    {
        var session = this.runtime.Current;
        if (session.HasPlatformReach() || session.TenantId is not { } tenantId || session.EffectiveUserId is not { } self)
            return Result<string?>.Failure(NoTenant);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            return Result<string?>.Success(await FirstNameAsync(context, tenantId, self, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the first name of user {UserId} failed.", self);
            return Result<string?>.Failure(ReadFailed);
        }
    }

    private static async Task<string?> FirstNameAsync(ApplicationDbContext context, Guid tenantId, Guid self, CancellationToken cancellationToken)
    {
        var name = await context.Users.AsNoTracking()
            .Where(user => user.TenantId == tenantId && user.Id == self)
            .Select(user => user.FirstName)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static async Task<HomeStaffing> StaffingAsync(ApplicationDbContext context, Guid tenantId, DateOnly today, DateTime now, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var monday = ScheduleRules.MondayOf(today);
        var (tuesday, wednesday, thursday, friday, saturday, sunday) = (monday.AddDays(1), monday.AddDays(2), monday.AddDays(3), monday.AddDays(4), monday.AddDays(5), monday.AddDays(6));

        var week = await context.Jobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && WeekJobStates.Contains(job.State))
            .Where(job => job.Schedule == JobSchedule.Single
                ? job.PeriodStart >= monday && job.PeriodStart <= sunday
                : job.PeriodStart <= sunday && job.PeriodEnd >= monday && job.TimeSlots.Any(time =>
                    (time.Weekday == Weekday.Monday && job.PeriodStart <= monday && job.PeriodEnd >= monday)
                    || (time.Weekday == Weekday.Tuesday && job.PeriodStart <= tuesday && job.PeriodEnd >= tuesday)
                    || (time.Weekday == Weekday.Wednesday && job.PeriodStart <= wednesday && job.PeriodEnd >= wednesday)
                    || (time.Weekday == Weekday.Thursday && job.PeriodStart <= thursday && job.PeriodEnd >= thursday)
                    || (time.Weekday == Weekday.Friday && job.PeriodStart <= friday && job.PeriodEnd >= friday)
                    || (time.Weekday == Weekday.Saturday && job.PeriodStart <= saturday && job.PeriodEnd >= saturday)
                    || (time.Weekday == Weekday.Sunday && job.PeriodStart <= sunday && job.PeriodEnd >= sunday)))
            .Select(job => new
            {
                Slots = job.Slots.Count(slot => slot.State == JobSlotState.Active),
                Held = job.Slots.Count(slot => slot.State == JobSlotState.Active && slot.Assignments.Any(assignment => assignment.State == SlotAssignmentState.Assigned)),
            })
            .ToListAsync(cancellationToken);

        var open = await context.Jobs.AsNoTracking()
            .AsSplitQuery()
            .Where(job => job.TenantId == tenantId
                && LiveJobStates.Contains(job.State)
                && job.Slots.Any(slot => slot.State == JobSlotState.Active && !slot.Assignments.Any(assignment => assignment.State == SlotAssignmentState.Assigned)))
            .Select(job => new
            {
                job.Schedule,
                job.PeriodStart,
                job.PeriodEnd,
                job.StartTime,
                job.EndTime,
                Open = job.Slots.Count(slot => slot.State == JobSlotState.Active && !slot.Assignments.Any(assignment => assignment.State == SlotAssignmentState.Assigned)),
                Weekly = job.TimeSlots.Select(time => new { time.Weekday, time.Start, time.End }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var soonEnd = today.AddDays(HomeRules.SoonDays - 1);
        var openSoon = open
            .Where(job => JobRules.Occurrences(new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, [.. job.Weekly.Select(time => new OccurrenceTimes(time.Weekday, time.Start, time.End))]), today, soonEnd, zone)
                .Any(occurrence => occurrence.EndUtc > now))
            .Sum(job => job.Open);

        var held = week.Sum(job => job.Held);
        return new HomeStaffing(week.Count, held, week.Sum(job => job.Slots) - held, open.Sum(job => job.Open), openSoon);
    }

    private static async Task<HomeAbsence> AbsenceAsync(ApplicationDbContext context, Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        var absentToday = context.ResourceAbsences.AsNoTracking().Where(absence => absence.TenantId == tenantId && absence.FirstDay <= today && absence.LastDay >= today
            && (absence.State == ResourceAbsenceState.Approved || (AbsenceRules.UndecidedStates.Contains(absence.State) && absence.Kind == AbsenceKind.SickLeave)));
        var waiting = WaitingAbsences(context, tenantId, today);

        var absentCount = await absentToday.CountAsync(cancellationToken);
        var waitingCount = await waiting.CountAsync(cancellationToken);
        var absentRows = absentCount == 0 ? [] : await AbsenceRows(absentToday).ToListAsync(cancellationToken);
        var waitingRows = waitingCount == 0 ? [] : await AbsenceRows(waiting).ToListAsync(cancellationToken);
        return new HomeAbsence(absentRows, absentCount, waitingRows, waitingCount);
    }

    private static Task<int> WithoutPatternAsync(ApplicationDbContext context, Guid tenantId, DateOnly today, CancellationToken cancellationToken) =>
        context.Resources.AsNoTracking()
            .Where(resource => resource.TenantId == tenantId)
            .WithoutPattern(context, today)
            .CountAsync(cancellationToken);

    private static IQueryable<ResourceAbsence> WaitingAbsences(ApplicationDbContext context, Guid tenantId, DateOnly today) =>
        context.ResourceAbsences.AsNoTracking().Where(absence => absence.TenantId == tenantId && AbsenceRules.UndecidedStates.Contains(absence.State) && absence.LastDay >= today);

    private static IQueryable<AbsenceRow> AbsenceRows(IQueryable<ResourceAbsence> rows) => rows
        .OrderBy(absence => absence.FirstDay)
        .ThenBy(absence => absence.Resource.User.FirstName)
        .ThenBy(absence => absence.Id)
        .Take(HomeRules.Shown)
        .Select(absence => new AbsenceRow(
            absence.Id,
            absence.Resource.UserId,
            (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Trim(),
            absence.Kind,
            absence.State,
            absence.FirstDay,
            absence.StartTime,
            absence.LastDay,
            absence.EndTime));

    private static async Task<HomeCustomers> CustomersAsync(ApplicationDbContext context, Guid tenantId, bool readsJobs, CancellationToken cancellationToken)
    {
        var counts = await context.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.State == CustomerState.Active)
            .GroupBy(customer => customer.LinkedTenantId != null)
            .Select(group => new { Portal = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var withPortal = counts.Where(count => count.Portal).Sum(count => count.Count);
        var customers = new HomeCustomers(counts.Sum(count => count.Count), withPortal);
        if (!readsJobs || withPortal == 0)
        {
            return customers;
        }

        var orders = await context.Jobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.LinkedCrossTenantId != null && (job.State == JobState.AwaitingCoordination || job.State == JobState.Coordinating))
            .GroupBy(job => job.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return customers with
        {
            Orders = new HomeOrders(
                orders.Where(order => order.State == JobState.AwaitingCoordination).Sum(order => order.Count),
                orders.Where(order => order.State == JobState.Coordinating).Sum(order => order.Count)),
        };
    }

    private static async Task<IReadOnlyList<HomeActivity>> ActivityAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var steps = await context.StateHistoryEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.EntityType == nameof(Job))
            .Where(entry => context.Jobs.Any(job => job.TenantId == tenantId && job.Id == entry.EntityId))
            .OrderByDescending(entry => entry.Created)
            .ThenByDescending(entry => entry.Id)
            .Take(HomeRules.Shown)
            .Select(entry => new
            {
                entry.EntityId,
                Job = context.Jobs.Where(job => job.TenantId == tenantId && job.Id == entry.EntityId).Select(job => job.Name).FirstOrDefault(),
                entry.FromState,
                entry.ToState,
                entry.Created,
                Actor = context.Users.Where(user => user.TenantId == tenantId && user.Id == entry.ActorId)
                    .Select(user => new { user.FirstName, user.LastName, user.IsSystemAccount })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return [.. steps.Select(step => new HomeActivity(
            step.EntityId,
            step.Job ?? string.Empty,
            (JobState)step.FromState,
            (JobState)step.ToState,
            step.Created,
            step.Actor is { IsSystemAccount: false } actor && $"{actor.FirstName} {actor.LastName}".Trim() is { Length: > 0 } name ? name : SystemActor))];
    }
}
