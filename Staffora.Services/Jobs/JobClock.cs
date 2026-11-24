using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Jobs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Coordination;
using Staffora.Services.Diagnostics;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;

namespace Staffora.Services.Jobs;

public sealed class JobClockOptions
{
    public const string Section = "JobClock";

    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(1);

    public bool Enabled { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed record JobClockPass(int Tenants, int Started, int Completed, int Failed, int Waiting = 0, int Expired = 0);

public sealed record JobClockFailure(int Failures, DateTimeOffset NextTry, DateTime RowStamp, string? Refusal);

public sealed partial class JobClock
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan LongestRetry = TimeSpan.FromHours(6);

    private readonly Lock gate = new();
    private readonly Dictionary<(Guid Tenant, Guid Job), JobClockFailure> failures = [];
    private readonly IServiceScopeFactory scopes;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly TimeProvider clock;
    private readonly TenantChangeBus changes;
    private readonly ILogger<JobClock> log;
    private readonly StafforaTelemetry telemetry;

    public JobClock(IServiceScopeFactory scopes, IApplicationDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock, TenantChangeBus changes, ILogger<JobClock> log, StafforaTelemetry telemetry)
    {
        this.scopes = scopes;
        this.dbFactory = dbFactory;
        this.clock = clock;
        this.changes = changes;
        this.log = log;
        this.telemetry = telemetry;
    }

    public static TimeSpan RetryAfter(int failures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        var doublings = Math.Min(failures - 1, 16);
        var wait = TimeSpan.FromTicks(FirstRetry.Ticks * (1L << doublings));
        return wait > LongestRetry ? LongestRetry : wait;
    }

    public JobClockFailure? FailureOf(Guid tenantId, Guid jobId)
    {
        lock (this.gate)
        {
            return this.failures.GetValueOrDefault((tenantId, jobId));
        }
    }

    public async Task<JobClockPass> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await ActiveTenantsAsync(cancellationToken);

        ForgetAllBut(tenants);
        var (started, completed, failed, waiting) = (0, 0, 0, 0);
        var (expiredRounds, expiredRequests) = (0, 0);
        foreach (var tenantId in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var moved = await RunForTenantAsync(tenantId, cancellationToken);
                started += moved.Started;
                completed += moved.Completed;
                failed += moved.Failed;
                waiting += moved.Waiting;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                this.log.LogError(exception, "The job clock could not go through tenant {TenantId}; it goes on with the other tenants.", tenantId);
            }

            try
            {
                var expiry = await ExpireRequestsAsync(tenantId, cancellationToken);
                expiredRounds += expiry.Rounds;
                expiredRequests += expiry.Requests;
                failed += expiry.Failed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                this.log.LogError(exception, "The job clock could not expire the requests of tenant {TenantId}; it goes on with the other tenants.", tenantId);
            }
        }

        this.telemetry.RecordJobClockPass(new Dictionary<string, int> { [JobWorkflowAttribute.PeriodStartsStep] = started, [JobWorkflowAttribute.PeriodEndsStep] = completed, [RequestRoundWorkflowAttribute.ExpireStep] = expiredRounds, [SlotAssignmentWorkflowAttribute.ExpireStep] = expiredRequests });
        return new JobClockPass(tenants.Count, started, completed, failed, waiting, expiredRounds + expiredRequests);
    }

    private async Task<List<Guid>> ActiveTenantsAsync(CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("Job clock: list the active tenants whose jobs it moves");
        return await context.Tenants.AsNoTracking()
            .Where(tenant => tenant.State == TenantState.Active && !tenant.IsRootTenant)
            .OrderBy(tenant => tenant.Id)
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
    }

    internal async Task<(int Started, int Completed, int Failed, int Waiting)> RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = this.scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runtime = ActAsSystemAccount(services.GetRequiredService<IRuntimeExecutionContext>(), tenantId);
        using var logScope = this.log.BeginScope("Job clock in tenant {TenantId}", tenantId);

        var zone = await services.GetRequiredService<TenantClock>().GetTimeZoneOfAsync(tenantId, cancellationToken);
        if (!zone.IsSuccess || zone.Value is not { } tenantZone)
        {
            throw new InvalidOperationException($"The tenant's time zone could not be read: {zone.Error}");
        }

        var now = this.clock.GetUtcNow();
        var today = JobRules.LocalDay(now, tenantZone);
        List<(Guid Id, DateTime Stamp)> due;
        await using (var context = this.dbFactory.CreateDbContext(runtime))
        {
            due = (await context.Jobs.AsNoTracking()
                    .Where(job => job.TenantId == tenantId
                        && ((job.State == JobState.Confirmed && job.PeriodStart <= today) || (job.State == JobState.InProgress && job.PeriodEnd <= today)))
                    .OrderBy(job => job.PeriodStart)
                    .ThenBy(job => job.Id)
                    .Select(job => new { job.Id, Stamp = job.Modified ?? job.Created, job.State, job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime })
                    .ToListAsync(cancellationToken))
                .Where(job => JobRules.ClockTarget(job.State, new JobTimes(job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, []), now.UtcDateTime, tenantZone) is not null)
                .Select(job => (job.Id, job.Stamp))
                .ToList();
        }

        Forget(tenantId, keep: [.. due.Select(job => job.Id)]);

        var workflows = services.GetRequiredService<WorkflowService>();
        var stepLocks = services.GetServices<IStepLock>().Where(stepLock => stepLock.EntityType.IsAssignableFrom(typeof(Job))).ToList();
        var (started, completed, failed, waiting) = (0, 0, 0, 0);
        foreach (var (jobId, stamp) in due)
        {
            if (FailureOf(tenantId, jobId) is { } earlier && earlier.RowStamp == stamp && now < earlier.NextTry)
            {
                waiting++;
                continue;
            }

            try
            {
                for (var step = 0; step < 2; step++)
                {
                    var moved = await StepAsync(runtime, workflows, stepLocks, jobId, tenantZone, cancellationToken);
                    if (moved is null)
                    {
                        break;
                    }

                    if (moved == JobState.InProgress)
                    {
                        started++;
                    }
                    else
                    {
                        completed++;
                    }
                }

                Forget(tenantId, jobId);
            }
            catch (SystemStepRefusedException refused)
            {
                failed++;
                var failure = Failed(tenantId, jobId, stamp, now, refused.Message);
                this.log.LogWarning("The job clock left job {JobId} of tenant {TenantId} as it is ({Failures} times in a row; tried again from {NextTry:o}): {Reason}", jobId, tenantId, failure.Failures, failure.NextTry, refused.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                var failure = Failed(tenantId, jobId, stamp, now, refusal: null);
                this.log.LogError(exception, "The job clock could not move job {JobId} of tenant {TenantId} ({Failures} times in a row; tried again from {NextTry:o}); it goes on with the other jobs.", jobId, tenantId, failure.Failures, failure.NextTry);
            }
        }

        if (started + completed > 0)
        {
            this.log.LogInformation("The job clock put {Started} jobs of tenant {TenantId} in progress and completed {Completed}.", started, tenantId, completed);
        }

        return (started, completed, failed, waiting);
    }

    private JobClockFailure Failed(Guid tenantId, Guid jobId, DateTime stamp, DateTimeOffset now, string? refusal)
    {
        lock (this.gate)
        {
            var failures = (this.failures.GetValueOrDefault((tenantId, jobId))?.Failures ?? 0) + 1;
            var failure = new JobClockFailure(failures, now + RetryAfter(failures), stamp, refusal);
            this.failures[(tenantId, jobId)] = failure;
            return failure;
        }
    }

    private void ForgetAllBut(IReadOnlyCollection<Guid> active)
    {
        var keep = active.ToHashSet();
        lock (this.gate)
        {
            foreach (var key in this.failures.Keys.Where(key => !keep.Contains(key.Tenant)).ToList())
            {
                this.failures.Remove(key);
            }
        }
    }

    private void Forget(Guid tenantId, Guid jobId)
    {
        lock (this.gate)
        {
            this.failures.Remove((tenantId, jobId));
        }
    }

    private void Forget(Guid tenantId, HashSet<Guid> keep)
    {
        lock (this.gate)
        {
            foreach (var key in this.failures.Keys.Where(key => key.Tenant == tenantId && !keep.Contains(key.Job)).ToList())
            {
                this.failures.Remove(key);
            }
        }
    }

    private static IRuntimeExecutionContext ActAsSystemAccount(IRuntimeExecutionContext runtime, Guid tenantId) =>
        SystemAccountSession.Apply(runtime, tenantId, "job clock");

    private async Task<JobState?> StepAsync(IRuntimeExecutionContext runtime, WorkflowService workflows, IReadOnlyList<IStepLock> stepLocks, Guid jobId, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(runtime);
        var touched = TenantChangeCollector.Attach(context, runtime.TenantId);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        foreach (var stepLock in stepLocks)
        {
            await stepLock.TakeAsync(context, jobId, cancellationToken);
        }

        var job = await context.Jobs.WithWorkflowGraph().AsTracking().FirstOrDefaultAsync(row => row.Id == jobId, cancellationToken);
        if (job is null || JobRules.ClockTarget(job.State, JobTimes.Of(job), this.clock.GetUtcNow().UtcDateTime, zone) is not { } target)
        {
            return null;
        }

        var step = target == JobState.InProgress ? JobWorkflowAttribute.PeriodStartsStep : JobWorkflowAttribute.PeriodEndsStep;
        await workflows.RunSystemStepInTransactionAsync<JobState, Job>(context, job, step, cancellationToken);
        if (target == JobState.Completed)
        {
            await CloseOpenRequestsAsync(workflows, context, jobId, cancellationToken);
        }

        await touched.NameAffectedAsync(context, this.log, cancellationToken);

        await transaction.CommitAsync(CancellationToken.None);

        try
        {
            touched.PublishTo(this.changes);
        }
        catch (Exception exception)
        {
            this.log.LogError(exception, "Publishing the job clock's step on job {JobId} failed after its commit; the step stands.", jobId);
        }

        return target;
    }

    private static async Task CloseOpenRequestsAsync(WorkflowService workflows, ApplicationDbContext context, Guid jobId, CancellationToken cancellationToken)
    {
        var open = await context.SlotAssignments.WithWorkflowGraph().AsTracking()
            .Where(assignment => assignment.Slot.JobId == jobId
                && (assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted))
            .OrderBy(assignment => assignment.Created).ThenBy(assignment => assignment.Id)
            .ToListAsync(cancellationToken);
        foreach (var assignment in open)
        {
            var step = SlotAssignmentWorkflowAttribute.NotSelectedStepFor(assignment.State)!;
            await workflows.RunSystemStepInTransactionAsync<SlotAssignmentState, SlotAssignment>(context, assignment, step, cancellationToken);
        }
    }
}

public sealed class JobClockService : BackgroundService
{
    private readonly JobClock jobClock;
    private readonly IOptions<JobClockOptions> options;
    private readonly TimeProvider clock;
    private readonly ILogger<JobClockService> log;

    public JobClockService(JobClock jobClock, IOptions<JobClockOptions> options, TimeProvider clock, ILogger<JobClockService> log)
    {
        this.jobClock = jobClock;
        this.options = options;
        this.clock = clock;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = this.options.Value;
        if (!settings.Enabled)
        {
            this.log.LogInformation("The job clock is off ({Section}:{Setting}); no job moves in progress or to completed by itself.", JobClockOptions.Section, nameof(JobClockOptions.Enabled));
            return;
        }

        this.log.LogInformation("The job clock runs every {Interval}.", settings.Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var pass = StafforaTelemetry.Source.StartActivity(StafforaTelemetry.JobClockPassActivity))
            {
                try
                {
                    await this.jobClock.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    pass?.SetStatus(ActivityStatusCode.Error);
                    this.log.LogError(exception, "A pass of the job clock failed; the next one tries again.");
                }
            }

            try
            {
                await Task.Delay(settings.Interval, this.clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
