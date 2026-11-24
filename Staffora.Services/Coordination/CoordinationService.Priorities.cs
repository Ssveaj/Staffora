using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Staffing;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Scheduling;

namespace Staffora.Services.Coordination;

public sealed partial class CoordinationService
{
    private static readonly JobState[] LiveJobStates = [JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress];

    public async Task<Result<PriorityBoard>> ReadPrioritiesAsync(CoordinationSnapshot snapshot, PriorityQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewCoordination, cancellationToken))
            return Result<PriorityBoard>.Failure(NoView);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<PriorityBoard>.Failure(NotOnConsole);

        if (session.TenantId is not { } tenantId)
            return Result<PriorityBoard>.Failure(IRuntimeExecutionContext.NoTenant);

        if (snapshot.TenantId != tenantId)
            return Result<PriorityBoard>.Failure(CoordinationService.NotThisTenant);

        try
        {
            var approves = await this.permissionService.HasPermissionAsync(RolePermission.ApproveResourceAbsence, cancellationToken);
            var readsAbsence = await this.permissionService.HasAnyPermissionAsync(ResourceAbsenceService.Readers, cancellationToken);
            return Result<PriorityBoard>.Success(await ReadPrioritiesOfTenantAsync(tenantId, session.EffectiveUserId, snapshot, query, approves, readsAbsence, cancellationToken));
        }
        catch (Exception exception)
        {
            ReadCancellation.ThrowIfReaderLeft(exception, this.log, "the coordination priorities", cancellationToken);
            this.log.LogError(exception, "Reading the coordination priorities of tenant {TenantId} failed.", tenantId);
            return Result<PriorityBoard>.Failure(ReadFailed);
        }
    }

    private async Task<PriorityBoard> ReadPrioritiesOfTenantAsync(Guid tenantId, Guid? reader, CoordinationSnapshot snapshot, PriorityQuery query, bool approves, bool readsAbsence, CancellationToken cancellationToken)
    {
        var zone = ZoneOf(snapshot.TimeZoneId);
        var now = this.clock.GetUtcNow().UtcDateTime;
        var today = snapshot.Today;
        var texts = new PriorityTexts(now, today, zone);
        var search = query.SearchText;
        var day = query.Day;
        var reads = new List<KindRead>();
        await using var context = this.dbFactory.CreateDbContext(this.runtime);

        int TakeOf(PriorityItemKind kind) => PriorityList.InTab(kind, query.Tab) ? query.Shown : 0;

        var slotsOfJob = snapshot.Entries.GroupBy(entry => entry.Row.JobId).ToDictionary(job => job.Key, job => job.Sum(entry => entry.Row.SlotCount));
        foreach (var (itemKind, kind) in new[] { (PriorityItemKind.AtRisk, PriorityKind.AtRisk), (PriorityItemKind.ToRequestOrAssign, PriorityKind.Uncovered), (PriorityItemKind.UnansweredRequests, PriorityKind.Unanswered), (PriorityItemKind.WaitingForPick, PriorityKind.WaitingForPick) })
        {
            var jobs = snapshot.Entries
                .Where(entry => entry.Row.Kind == kind)
                .Where(entry => Matches(search, entry.Row.Slot, entry.Row.Job, entry.Row.Customer))
                .Select(entry => (entry.Row, Occurrence: day is { } shown ? entry.Occurrences.FirstOrDefault(occurrence => occurrence.Date <= shown && occurrence.EndDate >= shown) : entry.Occurrences.FirstOrDefault()))
                .Where(found => day is null || found.Occurrence is not null)
                .GroupBy(found => found.Row.JobId)
                .Select(job => job.OrderBy(found => found.Occurrence?.StartUtc ?? found.Row.NextOccurrenceUtc ?? DateTime.MaxValue).ToList())
                .OrderBy(job => job[0].Occurrence?.StartUtc ?? job[0].Row.NextOccurrenceUtc ?? DateTime.MaxValue)
                .ThenBy(job => job[0].Row.JobId)
                .ToList();
            reads.Add(new KindRead(itemKind, jobs.Count, [.. jobs.Take(TakeOf(itemKind)).Select(job => JobItem(itemKind, [.. job.Select(found => found.Row)], job[0].Occurrence, slotsOfJob.GetValueOrDefault(job[0].Row.JobId), texts))]));
        }

        var behind = day is not null ? [] : snapshot.Behind.Where(job => Matches(search, job.Job)).OrderBy(job => job.JobId.ToString(), StringComparer.Ordinal).ToList();
        reads.Add(new KindRead(PriorityItemKind.JobsBehind, behind.Count, [.. behind.Take(TakeOf(PriorityItemKind.JobsBehind)).Select(job => new PriorityItem(
            PriorityItemKind.JobsBehind, $"behind-{job.JobId}", now, BehindWhen(job.State), job.Job, job.Reason, BehindMeta(job.State), PriorityTarget.Job, job.JobId) { IsSoon = true })]));

        reads.Add(await OrdersAsync(context, tenantId, query, TakeOf(PriorityItemKind.OrdersToAnswer), texts, cancellationToken));
        reads.Add(await CustomerMessagesAsync(context, tenantId, query, TakeOf(PriorityItemKind.CustomerMessages), texts, cancellationToken));
        reads.Add(await StaffQuestionsAsync(context, tenantId, reader, approves, query, TakeOf(PriorityItemKind.StaffQuestions), texts, cancellationToken));
        if (approves)
        {
            reads.Add(await AbsenceToApproveAsync(context, tenantId, reader, query, TakeOf(PriorityItemKind.AbsenceToApprove), texts, cancellationToken));
        }

        if (readsAbsence)
        {
            reads.Add(await AbsenceImpactAsync(context, tenantId, query, TakeOf(PriorityItemKind.AbsenceImpact), texts, cancellationToken));
        }

        var counts = Enum.GetValues<PriorityTab>().ToDictionary(tab => tab, tab => reads.Where(read => PriorityList.InTab(read.Kind, tab)).Sum(read => read.Total));
        var items = PriorityList.Order(reads.Where(read => PriorityList.InTab(read.Kind, query.Tab)).SelectMany(read => read.Items), now);
        return new PriorityBoard(query.Tab, [.. items.Take(query.Shown)], counts);
    }

    private static PriorityItem JobItem(PriorityItemKind kind, IReadOnlyList<CoordinationSlotRow> rows, OccurrenceInstant? occurrence, int slots, PriorityTexts texts)
    {
        var first = rows[0];
        var start = occurrence?.StartUtc ?? first.NextOccurrenceUtc;
        var count = kind switch
        {
            PriorityItemKind.AtRisk => $"{rows.Sum(row => Math.Max(1, row.AtRisk))} at risk",
            PriorityItemKind.UnansweredRequests => $"{rows.Sum(row => row.Requested)} asked, no answer",
            PriorityItemKind.WaitingForPick => $"{rows.Sum(row => row.Accepted)} said yes",
            _ => $"{rows.Sum(row => row.SlotCount - row.Assigned)} of {Math.Max(slots, rows.Sum(row => row.SlotCount))} open",
        };
        var openAgain = rows.Sum(row => row.OpenAgain);
        if (openAgain > 0)
        {
            count = $"{count}, {AbsenceImpactRules.OpenAgainText(openAgain, rows.Sum(row => row.SlotCount - row.Assigned))}";
        }

        return new PriorityItem(
            kind,
            $"job-{KindKey(kind)}-{first.JobId}",
            start ?? DateTime.MaxValue,
            start is { } at ? texts.When(at) : "No time left",
            first.Customer,
            $"{first.Job}, {count}",
            start is { } next ? texts.StartsIn(next) : first.StartsLabel,
            PriorityTarget.Job,
            first.JobId)
        { IsSoon = start is { } soon && texts.IsSoon(soon), IsOpenAgain = openAgain > 0 };
    }

    private static string KindKey(PriorityItemKind kind) => kind switch
    {
        PriorityItemKind.AtRisk => "risk",
        PriorityItemKind.UnansweredRequests => "unanswered",
        PriorityItemKind.WaitingForPick => "pick",
        _ => "open",
    };

    private static string BehindWhen(JobState state) => state == JobState.InProgress ? "Ended" : "Started";

    private static string BehindMeta(JobState state) => state switch
    {
        JobState.Coordinating => "Still coordinating",
        JobState.Confirmed => "Still confirmed",
        _ => "Still in progress",
    };

    private static async Task<KindRead> OrdersAsync(ApplicationDbContext context, Guid tenantId, PriorityQuery query, int take, PriorityTexts texts, CancellationToken cancellationToken)
    {
        var waiting = context.Jobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.LinkedCrossTenantId != null && job.State == JobState.AwaitingCoordination);
        if (query.SearchText is { Length: > 0 } search)
        {
            waiting = waiting.Where(job => job.Name.Contains(search) || job.Customer.Name.Contains(search));
        }

        if (query.Day is { } day)
        {
            waiting = waiting.Where(job => job.PeriodStart <= day && job.PeriodEnd >= day);
        }

        var total = await waiting.CountAsync(cancellationToken);
        if (total == 0 || take == 0)
        {
            return new KindRead(PriorityItemKind.OrdersToAnswer, total, []);
        }

        var soonest = waiting.OrderBy(job => job.PeriodStart).ThenBy(job => job.StartTime).ThenBy(job => job.Id).Take(take).Select(job => job.Id);
        var oldest = waiting.OrderBy(job => job.Created).ThenBy(job => job.Id).Take(take).Select(job => job.Id);
        var rows = await waiting
            .Where(job => soonest.Contains(job.Id) || oldest.Contains(job.Id))
            .Select(job => new
            {
                job.Id,
                job.Name,
                Customer = job.Customer.Name,
                job.Created,
                job.Schedule,
                job.PeriodStart,
                job.PeriodEnd,
                job.StartTime,
                job.EndTime,
                Weekly = job.TimeSlots.Select(time => new { time.Weekday, time.Start, time.End }).ToList(),
                Staff = job.Slots.Count(slot => slot.State == JobSlotState.Active),
            })
            .ToListAsync(cancellationToken);
        return new KindRead(PriorityItemKind.OrdersToAnswer, total, [.. rows.Select(job =>
        {
            var times = new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, [.. job.Weekly.Select(time => new OccurrenceTimes(time.Weekday, time.Start, time.End))]);
            var first = JobRules.Occurrences(times, job.PeriodStart, job.PeriodStart.AddDays(6), texts.Zone).FirstOrDefault();
            var start = first?.StartUtc ?? WallClock.ToUtc(job.PeriodStart, TimeOnly.MinValue, texts.Zone);
            var staff = job.Staff == 1 ? "1 staff" : $"{job.Staff} staff";
            return new PriorityItem(
                PriorityItemKind.OrdersToAnswer,
                $"order-{job.Id}",
                PriorityList.DueUtc(start, job.Created),
                first is null ? texts.Day(job.PeriodStart) : texts.When(start),
                job.Customer,
                $"{job.Name}, {staff}",
                $"Waited {texts.Duration(texts.Now - job.Created)}",
                PriorityTarget.Job,
                job.Id)
            { IsSoon = texts.IsSoon(start) };
        })]);
    }

    private static async Task<KindRead> CustomerMessagesAsync(ApplicationDbContext context, Guid tenantId, PriorityQuery query, int take, PriorityTexts texts, CancellationToken cancellationToken)
    {
        if (query.Day is not null)
        {
            return new KindRead(PriorityItemKind.CustomerMessages, 0, []);
        }

        var jobs = context.Jobs.Where(job => job.TenantId == tenantId && LiveJobStates.Contains(job.State));
        if (query.SearchText is { Length: > 0 } search)
        {
            jobs = jobs.Where(job => job.Name.Contains(search) || job.Customer.Name.Contains(search));
        }

        var awaiting = context.CommentContainers.AsNoTracking()
            .Where(thread => thread.TenantId == tenantId
                && thread.EntityType == nameof(Job)
                && thread.Audience == CommentAudience.Customer
                && thread.Comments
                    .OrderByDescending(comment => comment.Created)
                    .ThenByDescending(comment => comment.Id)
                    .Select(comment => comment.Kind == CommentKind.Comment && comment.AuthorType == CommentAuthorType.Contact)
                    .FirstOrDefault())
            .Join(jobs, thread => thread.EntityId, job => job.Id, (thread, job) => new { job.Id, job.Name, Customer = job.Customer.Name, At = thread.Comments.Max(comment => comment.Created) });
        var total = await awaiting.CountAsync(cancellationToken);
        var rows = total == 0 || take == 0 ? [] : await awaiting
            .OrderBy(row => row.At)
            .ThenBy(row => row.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        return new KindRead(PriorityItemKind.CustomerMessages, total, [.. rows.Select(row => new PriorityItem(
            PriorityItemKind.CustomerMessages,
            $"customer-{row.Id}",
            PriorityList.DueUtc(null, row.At),
            texts.When(row.At),
            row.Customer,
            row.Name,
            $"Wrote {texts.Duration(texts.Now - row.At)} ago",
            PriorityTarget.Job,
            row.Id))]);
    }

    private static async Task<KindRead> StaffQuestionsAsync(ApplicationDbContext context, Guid tenantId, Guid? reader, bool approves, PriorityQuery query, int take, PriorityTexts texts, CancellationToken cancellationToken)
    {
        var items = new List<PriorityItem>();
        var total = 0;
        if (approves)
        {
            var asked = AbsencesOf(context, tenantId, reader, query).Where(absence => absence.State == ResourceAbsenceState.StaffQuestion);
            total += await asked.CountAsync(cancellationToken);
            if (take > 0)
            {
                var soonest = asked.OrderBy(absence => absence.FirstDay).ThenBy(absence => absence.StartTime).ThenBy(absence => absence.Id).Take(take).Select(absence => absence.Id);
                var oldest = asked.OrderBy(absence => absence.Modified ?? absence.Created).ThenBy(absence => absence.Id).Take(take).Select(absence => absence.Id);
                items.AddRange((await ReadAbsencesAsync(asked.Where(absence => soonest.Contains(absence.Id) || oldest.Contains(absence.Id)), cancellationToken))
                    .Select(absence => AbsenceItem(PriorityItemKind.StaffQuestions, absence, texts, "Asked", waitsForReply: true)));
            }
        }

        if (query.Day is null)
        {
            var messages = context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId
                    && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned)
                    && assignment.Slot.State == JobSlotState.Active
                    && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress)
                    && (assignment.State == SlotAssignmentState.StaffQuestion
                        || (assignment.LastStaffCommentAt != null
                            && (assignment.LastCoordinatorCommentAt == null || assignment.LastStaffCommentAt > assignment.LastCoordinatorCommentAt))));
            if (query.SearchText is { Length: > 0 } search)
            {
                messages = messages.Where(assignment => assignment.Slot.Name.Contains(search) || assignment.Slot.Job.Name.Contains(search) || (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Contains(search));
            }

            total += await messages.CountAsync(cancellationToken);
            var rows = take == 0 ? [] : await messages
                .OrderBy(assignment => assignment.State == SlotAssignmentState.StaffQuestion ? assignment.Modified ?? assignment.Created : assignment.LastStaffCommentAt)
                .ThenBy(assignment => assignment.Id)
                .Take(take)
                .Select(assignment => new
                {
                    assignment.Id,
                    assignment.Slot.JobId,
                    Name = (assignment.Resource.User.FirstName + " " + assignment.Resource.User.LastName).Trim(),
                    assignment.Resource.User.Email,
                    Slot = assignment.Slot.Name,
                    Job = assignment.Slot.Job.Name,
                    Asked = assignment.State == SlotAssignmentState.StaffQuestion,
                    At = assignment.State == SlotAssignmentState.StaffQuestion ? assignment.Modified ?? assignment.Created : assignment.LastStaffCommentAt!.Value,
                })
                .ToListAsync(cancellationToken);
            items.AddRange(rows.Select(row =>
            {
                var name = NameOf(row.Name, row.Email);
                return new PriorityItem(
                    PriorityItemKind.StaffQuestions,
                    row.Asked ? $"question-{row.Id}" : $"message-{row.Id}",
                    PriorityList.DueUtc(null, row.At),
                    texts.When(row.At),
                    name,
                    $"{row.Slot}, {row.Job}",
                    row.Asked ? $"Question waiting for {texts.Duration(texts.Now - row.At)}" : $"Wrote {texts.Duration(texts.Now - row.At)} ago",
                    PriorityTarget.Job,
                    row.JobId);
            }));
        }

        return new KindRead(PriorityItemKind.StaffQuestions, total, items);
    }

    private static async Task<KindRead> AbsenceToApproveAsync(ApplicationDbContext context, Guid tenantId, Guid? reader, PriorityQuery query, int take, PriorityTexts texts, CancellationToken cancellationToken)
    {
        var requested = AbsencesOf(context, tenantId, reader, query).Where(absence => absence.State == ResourceAbsenceState.Requested && absence.Kind == AbsenceKind.PlannedLeave);
        var total = await requested.CountAsync(cancellationToken);
        var rows = total == 0 || take == 0 ? [] : await ReadAbsencesAsync(requested.OrderBy(absence => absence.FirstDay).ThenBy(absence => absence.StartTime).ThenBy(absence => absence.Id).Take(take), cancellationToken);
        return new KindRead(PriorityItemKind.AbsenceToApprove, total, [.. rows.Select(absence => AbsenceItem(PriorityItemKind.AbsenceToApprove, absence, texts, "Requested", waitsForReply: false))]);
    }

    private static async Task<KindRead> AbsenceImpactAsync(ApplicationDbContext context, Guid tenantId, PriorityQuery query, int take, PriorityTexts texts, CancellationToken cancellationToken)
    {
        var today = texts.Today;
        var blocking = context.ResourceAbsences.AsNoTracking()
            .Where(absence => absence.TenantId == tenantId
                && (absence.State == ResourceAbsenceState.Approved || (AbsenceRules.UndecidedStates.Contains(absence.State) && absence.Kind == AbsenceKind.SickLeave))
                && absence.LastDay >= today);
        if (query.Day is { } day)
        {
            blocking = blocking.Where(absence => absence.FirstDay <= day && absence.LastDay >= day);
        }

        if (query.SearchText is { Length: > 0 } search)
        {
            blocking = blocking.Where(absence => (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Contains(search));
        }

        var candidates = await blocking
            .Where(absence => context.SlotAssignments.Any(assignment => assignment.TenantId == tenantId
                && assignment.ResourceId == absence.ResourceId
                && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned)
                && assignment.Slot.State == JobSlotState.Active
                && (assignment.Slot.Job.State == JobState.Coordinating || assignment.Slot.Job.State == JobState.Confirmed || assignment.Slot.Job.State == JobState.InProgress)
                && assignment.Slot.Job.PeriodStart <= absence.LastDay
                && assignment.Slot.Job.PeriodEnd >= absence.FirstDay.AddDays(-1)))
            .OrderBy(absence => absence.FirstDay)
            .ThenBy(absence => absence.Id)
            .Take(MaxImpactReads)
            .Select(absence => new
            {
                Scheduled = new ScheduledAbsence(absence.Id, absence.ResourceId, absence.Kind, absence.State, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime),
                Name = (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Trim(),
                absence.Resource.User.Email,
            })
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return new KindRead(PriorityItemKind.AbsenceImpact, 0, []);
        }

        var impact = await AbsenceImpactReader.ReadAsync(context, tenantId, [.. candidates.Select(candidate => candidate.Scheduled)], texts.Now, texts.Zone, withTakenOff: false, cancellationToken);
        var affected = candidates
            .Select(candidate => (Candidate: candidate, Rows: impact[candidate.Scheduled.AbsenceId]
                .Where(row => query.Day is not { } shown || row.Occurrences.Any(occurrence => occurrence.Date <= shown && occurrence.EndDate >= shown))
                .ToList()))
            .Where(found => found.Rows.Count > 0)
            .OrderBy(found => found.Rows.Min(row => row.FirstUtc))
            .ThenBy(found => found.Candidate.Scheduled.AbsenceId)
            .ToList();
        return new KindRead(PriorityItemKind.AbsenceImpact, affected.Count, [.. affected.Take(take).Select(found =>
        {
            var absence = found.Candidate.Scheduled;
            var first = found.Rows.MinBy(row => row.FirstUtc)!;
            var jobs = found.Rows.Select(row => row.JobId).Distinct().Count();
            var leave = absence.Kind == AbsenceKind.SickLeave ? "Sick leave" : "Planned leave";
            return new PriorityItem(
                PriorityItemKind.AbsenceImpact,
                $"impact-{absence.AbsenceId}",
                first.FirstUtc,
                texts.When(first.FirstUtc),
                NameOf(found.Candidate.Name, found.Candidate.Email),
                $"{leave} {AbsenceRules.PeriodText(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime)}, {(jobs == 1 ? $"{first.Slot}, {first.Job}" : $"{jobs} jobs")}",
                found.Rows.Count == 1 ? "1 assignment to settle" : $"{found.Rows.Count} assignments to settle",
                PriorityTarget.AbsenceImpact,
                absence.AbsenceId,
                absence.AbsenceId)
            { IsSoon = texts.IsSoon(first.FirstUtc) };
        })]);
    }

    private const int MaxImpactReads = 200;

    private static IQueryable<ResourceAbsence> AbsencesOf(ApplicationDbContext context, Guid tenantId, Guid? reader, PriorityQuery query)
    {
        var absences = context.ResourceAbsences.AsNoTracking().Where(absence => absence.TenantId == tenantId && absence.Resource.UserId != reader);
        if (query.Day is { } day)
        {
            absences = absences.Where(absence => absence.FirstDay <= day && absence.LastDay >= day);
        }

        if (query.SearchText is { Length: > 0 } search)
        {
            absences = absences.Where(absence => (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Contains(search));
        }

        return absences;
    }

    private static async Task<IReadOnlyList<AbsenceFacts>> ReadAbsencesAsync(IQueryable<ResourceAbsence> absences, CancellationToken cancellationToken) =>
        [.. (await absences
                .Select(absence => new
                {
                    absence.Id,
                    absence.Kind,
                    absence.FirstDay,
                    absence.StartTime,
                    absence.LastDay,
                    absence.EndTime,
                    At = absence.Modified ?? absence.Created,
                    Name = (absence.Resource.User.FirstName + " " + absence.Resource.User.LastName).Trim(),
                    absence.Resource.User.Email,
                })
                .ToListAsync(cancellationToken))
            .Select(absence => new AbsenceFacts(absence.Id, absence.Kind, absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime, absence.At, NameOf(absence.Name, absence.Email)))];

    private static PriorityItem AbsenceItem(PriorityItemKind kind, AbsenceFacts absence, PriorityTexts texts, string verb, bool waitsForReply)
    {
        var start = AbsenceRules.Instants(absence.FirstDay, absence.StartTime, absence.LastDay, absence.EndTime, texts.Zone).StartUtc;
        var leave = absence.Kind == AbsenceKind.SickLeave ? "Sick leave" : "Planned leave";
        return new PriorityItem(
            kind,
            $"absence-{absence.Id}",
            PriorityList.DueUtc(start, waitsForReply ? absence.At : null),
            texts.Day(absence.FirstDay),
            absence.Name,
            $"{leave} {absence.Period}",
            $"{verb} {texts.Duration(texts.Now - absence.At)} ago",
            PriorityTarget.Absence,
            absence.Id)
        { IsSoon = texts.IsSoon(start) };
    }

    private static bool Matches(string search, params string?[] names) =>
        search.Length == 0 || names.Any(name => name?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true);

    private static TimeZoneInfo ZoneOf(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private sealed record KindRead(PriorityItemKind Kind, int Total, IReadOnlyList<PriorityItem> Items);

    private sealed record AbsenceFacts(Guid Id, AbsenceKind Kind, DateOnly FirstDay, TimeOnly? StartTime, DateOnly LastDay, TimeOnly? EndTime, DateTime At, string Name)
    {
        public string Period => AbsenceRules.PeriodText(FirstDay, StartTime, LastDay, EndTime);
    }

    private sealed class PriorityTexts
    {
        public PriorityTexts(DateTime now, DateOnly today, TimeZoneInfo zone)
        {
            Now = now;
            Today = today;
            Zone = zone;
        }

        public DateTime Now { get; }

        public DateOnly Today { get; }

        public TimeZoneInfo Zone { get; }

        public string When(DateTime utc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
            return $"{Day(DateOnly.FromDateTime(local))} {local.ToString("HH':'mm", CultureInfo.InvariantCulture)}";
        }

        public string Day(DateOnly day) => (day.DayNumber - Today.DayNumber) switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => Math.Abs(day.DayNumber - Today.DayNumber) < 7 ? day.ToString("ddd d", CultureInfo.InvariantCulture) : day.ToString("ddd d MMM", CultureInfo.InvariantCulture),
        };

        public string StartsIn(DateTime utc) => utc <= Now ? "Started" : $"Starts in {Duration(utc - Now)}";

        public string Duration(TimeSpan span) =>
            span.TotalMinutes < 60 ? $"{Math.Max(1, (int)span.TotalMinutes)} min"
            : span.TotalHours < 48 ? $"{(int)span.TotalHours} h"
            : (int)span.TotalDays == 1 ? "1 day" : $"{(int)span.TotalDays} days";

        public bool IsSoon(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone)) <= Today;
    }
}
