using System.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Diagnostics;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class JobClockTests : IClassFixture<RealDatabase>
{
    private static readonly string[] Confirmed = [JobWorkflowAttribute.RegisterStep, JobWorkflowAttribute.StartFromRegistrationStep, JobWorkflowAttribute.ConfirmStep];

    private readonly RealDatabase database;

    public JobClockTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fake_clock_moves_a_confirmed_job_in_progress_at_its_periods_first_day_and_completed_after_its_last_with_a_history_row_each()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock Staffing");
        var job = await ConfirmedJobAsync(scenario, coordinator, "Spring algebra", new DateOnly(2027, 3, 28), new DateOnly(2027, 4, 30));
        var account = SystemAccount.IdFor(scenario.TenantId);

        var clock = new FakeClock(At("2027-03-27T22:59:59Z"));
        await using var host = ClockHost(clock);
        var jobClock = host.Get<JobClock>();
        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.Confirmed, (await StoredJobAsync(job)).State);
        Assert.Equal(Confirmed, (await HistoryAsync(job)).Select(entry => entry.TransitionKey));

        clock.Advance(TimeSpan.FromSeconds(1));
        await jobClock.RunOnceAsync(Ct);
        var started = await StoredJobAsync(job);
        Assert.Equal((JobState.InProgress, (Guid?)account), (started.State, started.ModifiedById));
        var startedRow = (await HistoryAsync(job))[^1];
        Assert.Equal((JobWorkflowAttribute.PeriodStartsStep, (int)JobState.Confirmed, (int)JobState.InProgress, account, account, (Guid?)null, scenario.TenantId), (startedRow.TransitionKey, startedRow.FromState, startedRow.ToState, startedRow.ActorId, startedRow.CreatedById, startedRow.ImpersonatedById, startedRow.TenantId));

        await jobClock.RunOnceAsync(Ct);
        Assert.Equal([.. Confirmed, JobWorkflowAttribute.PeriodStartsStep], (await HistoryAsync(job)).Select(entry => entry.TransitionKey));

        clock.Now = At("2027-04-30T21:59:59Z");
        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.InProgress, (await StoredJobAsync(job)).State);

        clock.Advance(TimeSpan.FromSeconds(1));
        await jobClock.RunOnceAsync(Ct);
        Assert.Equal(JobState.Completed, (await StoredJobAsync(job)).State);
        var history = await HistoryAsync(job);
        Assert.Equal([.. Confirmed, JobWorkflowAttribute.PeriodStartsStep, JobWorkflowAttribute.PeriodEndsStep], history.Select(entry => entry.TransitionKey));
        Assert.Equal(((int)JobState.InProgress, (int)JobState.Completed, account, account), (history[^1].FromState, history[^1].ToState, history[^1].ActorId, history[^1].CreatedById));
    }

    [Fact]
    public async Task The_clock_writes_nothing_in_another_tenant_and_stamps_each_tenants_rows_with_its_own_system_account()
    {
        var (first, firstCoordinator) = await ArrangeAsync("Clock First Staffing");
        var (second, secondCoordinator) = await ArrangeAsync("Clock Second Staffing");
        var firstJob = await ConfirmedJobAsync(first, firstCoordinator, "First job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));
        var secondJob = await ConfirmedJobAsync(second, secondCoordinator, "Second job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));
        await using var host = ClockHost(new FakeClock(At("2027-03-10T12:00:00Z")));

        var before = await TenantWritesAsync(second.TenantId);
        var moved = await host.Get<JobClock>().RunForTenantAsync(first.TenantId, Ct);
        Assert.Equal((1, 0, 0, 0), moved);
        Assert.Equal(JobState.InProgress, (await StoredJobAsync(firstJob)).State);
        Assert.Equal(JobState.Confirmed, (await StoredJobAsync(secondJob)).State);
        Assert.Equal(before, await TenantWritesAsync(second.TenantId));

        await host.Get<JobClock>().RunOnceAsync(Ct);
        foreach (var (tenant, job, other) in new[] { (first.TenantId, firstJob, second.TenantId), (second.TenantId, secondJob, first.TenantId) })
        {
            var stored = await StoredJobAsync(job);
            var row = (await HistoryAsync(job))[^1];
            Assert.Equal((JobState.InProgress, (Guid?)SystemAccount.IdFor(tenant)), (stored.State, stored.ModifiedById));
            Assert.Equal((JobWorkflowAttribute.PeriodStartsStep, tenant, SystemAccount.IdFor(tenant), SystemAccount.IdFor(tenant)), (row.TransitionKey, row.TenantId, row.ActorId, row.CreatedById));
            await using var check = this.database.CreateSystemContext();
            Assert.False(await check.StateHistoryEntries.AnyAsync(entry => entry.TenantId == tenant && (entry.CreatedById == SystemAccount.IdFor(other) || entry.ActorId == SystemAccount.IdFor(other)), Ct));
            Assert.False(await check.Jobs.AnyAsync(row => row.TenantId == tenant && row.ModifiedById == SystemAccount.IdFor(other), Ct));
        }
    }

    [Theory]
    [InlineData(TenantState.Suspended)]
    [InlineData(TenantState.PendingDeletion)]
    [InlineData(TenantState.Archived)]
    [InlineData(TenantState.Setup)]
    public async Task A_tenant_that_is_not_active_is_skipped_and_nothing_of_it_is_written(TenantState state)
    {
        var (scenario, coordinator) = await ArrangeAsync($"Clock {state} Staffing");
        var job = await ConfirmedJobAsync(scenario, coordinator, "Held job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));
        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)state} WHERE Id = {scenario.TenantId}", Ct);
        }

        var before = await TenantWritesAsync(scenario.TenantId);
        var logged = new List<string>();
        await using var host = ClockHost(new FakeClock(At("2027-03-10T12:00:00Z")), services => services.AddLogging(logging => logging.AddProvider(new MessageRecorder(logged))));
        var pass = await host.Get<JobClock>().RunOnceAsync(Ct);

        Assert.Equal(JobState.Confirmed, (await StoredJobAsync(job)).State);
        Assert.Equal(before, await TenantWritesAsync(scenario.TenantId));
        Assert.Equal(0, pass.Failed);
        Assert.DoesNotContain(logged, message => message.Contains(scenario.TenantId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_missed_pass_is_caught_up_in_order_and_a_job_still_being_coordinated_is_left_alone()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock Catch-up Staffing");
        var ended = await ConfirmedJobAsync(scenario, coordinator, "Ended job", new DateOnly(2027, 1, 11), new DateOnly(2027, 2, 26));
        Guid coordinating;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            coordinating = await CoordinatingJobAsync(keeper, scenario, "Unconfirmed job", new DateOnly(2027, 1, 11), new DateOnly(2027, 2, 26));
        }

        await using var host = ClockHost(new FakeClock(At("2027-06-01T08:00:00Z")));
        await host.Get<JobClock>().RunOnceAsync(Ct);

        Assert.Equal(JobState.Completed, (await StoredJobAsync(ended)).State);
        var history = await HistoryAsync(ended);
        Assert.Equal([.. Confirmed, JobWorkflowAttribute.PeriodStartsStep, JobWorkflowAttribute.PeriodEndsStep], history.Select(entry => entry.TransitionKey));
        Assert.Equal([((int)JobState.Confirmed, (int)JobState.InProgress), ((int)JobState.InProgress, (int)JobState.Completed)], history.TakeLast(2).Select(entry => (entry.FromState, entry.ToState)));
        Assert.Equal(JobState.Coordinating, (await StoredJobAsync(coordinating)).State);
        Assert.Equal([JobWorkflowAttribute.RegisterStep, JobWorkflowAttribute.StartFromRegistrationStep], (await HistoryAsync(coordinating)).Select(entry => entry.TransitionKey));
    }

    [Fact(Timeout = 180000)]
    public async Task Two_passes_at_once_as_two_instances_of_the_app_would_run_them_move_each_job_once()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock Twin Staffing");
        var jobs = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            jobs.Add(await ConfirmedJobAsync(scenario, coordinator, $"Twin job {i}", new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 31)));
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var one = ClockHost(new FakeClock(At("2027-04-02T12:00:00Z")));
        await using var two = ClockHost(new FakeClock(At("2027-04-02T12:00:00Z")));
        var passes = await Task.WhenAll(one.Get<JobClock>().RunOnceAsync(cancellationToken), two.Get<JobClock>().RunOnceAsync(cancellationToken));

        Assert.All(passes, pass => Assert.Equal(0, pass.Failed));
        foreach (var job in jobs)
        {
            Assert.Equal(JobState.Completed, (await StoredJobAsync(job)).State);
            Assert.Equal([.. Confirmed, JobWorkflowAttribute.PeriodStartsStep, JobWorkflowAttribute.PeriodEndsStep], (await HistoryAsync(job)).Select(entry => entry.TransitionKey));
        }
    }

    [Fact]
    public async Task A_tenants_new_time_zone_counts_at_the_next_pass()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock Zone Staffing");
        var job = await ConfirmedJobAsync(scenario, coordinator, "Zone job", new DateOnly(2027, 3, 28), new DateOnly(2027, 5, 28));
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());

        await using var host = ClockHost(new FakeClock(At("2027-03-27T15:30:00Z")), ServiceHost.SharingLookupCacheOf(owner));
        await host.Get<JobClock>().RunOnceAsync(Ct);
        Assert.Equal(JobState.Confirmed, (await StoredJobAsync(job)).State);

        var settings = owner.Get<TenantSettingsService>();
        var loaded = await settings.LoadAsync(Ct);
        Assert.True(loaded.IsSuccess, loaded.Error);
        var saved = await settings.SaveAsync(TenantSettingDefinitions.TimeZone.Key, "Asia/Tokyo", loaded.Value!.Of(TenantSettingDefinitions.TimeZone).Version, Ct);
        Assert.True(saved.IsSuccess, saved.Error);

        await host.Get<JobClock>().RunOnceAsync(Ct);
        Assert.Equal(JobState.InProgress, (await StoredJobAsync(job)).State);
    }

    [Fact]
    public async Task A_job_that_cannot_be_moved_is_logged_to_the_error_log_and_the_other_tenants_jobs_still_move()
    {
        var (one, oneCoordinator) = await ArrangeAsync("Clock Broken Staffing");
        var (two, twoCoordinator) = await ArrangeAsync("Clock Sound Staffing");

        var (failing, failingCoordinator, sound, soundCoordinator) = new SqlGuid(one.TenantId).CompareTo(new SqlGuid(two.TenantId)) < 0
            ? (one, oneCoordinator, two, twoCoordinator)
            : (two, twoCoordinator, one, oneCoordinator);
        var broken = await ConfirmedJobAsync(failing, failingCoordinator, "Broken job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));
        var fine = await ConfirmedJobAsync(sound, soundCoordinator, "Sound job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));

        await using var host = ClockHost(new FakeClock(At("2027-03-10T12:00:00Z")), services =>
        {
            services.AddErrorLog();
            services.AddSingleton<IStepPreparation>(new FailsFor(broken));
        });
        var writer = host.Get<ErrorLogWriter>();
        await writer.StartAsync(Ct);
        JobClockPass pass;
        try
        {
            pass = await host.Get<JobClock>().RunOnceAsync(Ct);
            await host.Get<ErrorLogQueue>().FlushAsync(Ct);
        }
        finally
        {
            await writer.StopAsync(Ct);
        }

        Assert.True(pass.Failed >= 1);
        Assert.Equal(JobState.Confirmed, (await StoredJobAsync(broken)).State);
        Assert.Equal(JobState.InProgress, (await StoredJobAsync(fine)).State);
        await using var check = this.database.CreateSystemContext();
        var errors = await check.ErrorLogs.AsNoTracking().Where(log => log.Category == typeof(JobClock).FullName).ToListAsync(Ct);
        var error = Assert.Single(errors, log => log.Message.Contains(broken.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ErrorLogLevel.Error, error.Level);
    }

    [Fact]
    public async Task A_job_the_clock_cannot_move_waits_longer_and_longer_before_it_is_tried_and_logged_again_and_at_once_when_its_row_changes()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock Backoff Staffing");
        var broken = await ConfirmedJobAsync(scenario, coordinator, "Backoff job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));
        var failing = new Breakable(broken);
        var start = At("2027-03-10T12:00:00Z");
        var clock = new FakeClock(start);
        var logged = new List<string>();
        await using var host = ClockHost(clock, services =>
        {
            services.AddSingleton<IStepPreparation>(failing);
            services.AddLogging(logging => logging.AddProvider(new MessageRecorder(logged)));
        });
        var jobClock = host.Get<JobClock>();
        int Tries() => logged.Count(message => message.Contains(broken.ToString(), StringComparison.OrdinalIgnoreCase) && message.Contains("could not move", StringComparison.Ordinal));

        Assert.Equal((0, 0, 1, 0), await jobClock.RunForTenantAsync(scenario.TenantId, Ct));
        Assert.Equal(1, Tries());
        Assert.Equal((1, start + JobClock.FirstRetry, (string?)null), (jobClock.FailureOf(scenario.TenantId, broken)!.Failures, jobClock.FailureOf(scenario.TenantId, broken)!.NextTry, jobClock.FailureOf(scenario.TenantId, broken)!.Refusal));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal((0, 0, 0, 1), await jobClock.RunForTenantAsync(scenario.TenantId, Ct));
        clock.Now = start + JobClock.FirstRetry - TimeSpan.FromSeconds(1);
        Assert.Equal((0, 0, 0, 1), await jobClock.RunForTenantAsync(scenario.TenantId, Ct));
        Assert.Equal(1, Tries());

        clock.Now = start + JobClock.FirstRetry;
        Assert.Equal((0, 0, 1, 0), await jobClock.RunForTenantAsync(scenario.TenantId, Ct));
        Assert.Equal(2, Tries());
        var second = jobClock.FailureOf(scenario.TenantId, broken)!;
        Assert.Equal((2, clock.Now + TimeSpan.FromMinutes(10)), (second.Failures, second.NextTry));
        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), JobClock.LongestRetry, JobClock.LongestRetry], new[] { 1, 2, 3, 8, 40 }.Select(JobClock.RetryAfter));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal((0, 0, 0, 1), await jobClock.RunForTenantAsync(scenario.TenantId, Ct));

        failing.Broken = false;
        await using (var editor = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var jobs = editor.Get<JobService>();
            var updater = await jobs.LoadJobUpdaterAsync(broken, Ct);
            updater.Value!.Entity.Description = "Saved while the clock waited";
            Assert.True((await jobs.UpdateJobAndTransitionAsync(updater.Value!, JobWorkflowAttribute.SaveConfirmedStep, null, Ct)).IsSuccess);
        }

        failing.Broken = true;
        Assert.Equal((0, 0, 1, 0), await jobClock.RunForTenantAsync(scenario.TenantId, Ct));
        Assert.Equal(3, Tries());

        failing.Broken = false;
        clock.Advance(JobClock.LongestRetry);
        Assert.Equal((1, 0, 0, 0), await jobClock.RunForTenantAsync(scenario.TenantId, Ct));
        Assert.Equal(JobState.InProgress, (await StoredJobAsync(broken)).State);
        Assert.Null(jobClock.FailureOf(scenario.TenantId, broken));
    }

    [Fact]
    public async Task What_the_clock_remembers_of_a_tenant_that_is_no_longer_active_is_forgotten_at_the_next_pass()
    {
        var (held, heldCoordinator) = await ArrangeAsync("Clock Forget Held Staffing");
        var (active, activeCoordinator) = await ArrangeAsync("Clock Forget Active Staffing");
        var heldJob = await ConfirmedJobAsync(held, heldCoordinator, "Held failing job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));
        var activeJob = await ConfirmedJobAsync(active, activeCoordinator, "Active failing job", new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 30));
        var clock = new FakeClock(At("2027-03-10T12:00:00Z"));
        await using var host = ClockHost(clock, services =>
        {
            services.AddSingleton<IStepPreparation>(new FailsFor(heldJob));
            services.AddSingleton<IStepPreparation>(new FailsFor(activeJob));
        });
        var jobClock = host.Get<JobClock>();

        Assert.Equal((0, 0, 1, 0), await jobClock.RunForTenantAsync(held.TenantId, Ct));
        Assert.Equal((0, 0, 1, 0), await jobClock.RunForTenantAsync(active.TenantId, Ct));
        Assert.NotNull(jobClock.FailureOf(held.TenantId, heldJob));
        Assert.NotNull(jobClock.FailureOf(active.TenantId, activeJob));

        await using (var system = this.database.CreateSystemContext())
        {
            await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)TenantState.Suspended} WHERE Id = {held.TenantId}", Ct);
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        await jobClock.RunOnceAsync(Ct);
        Assert.Null(jobClock.FailureOf(held.TenantId, heldJob));
        Assert.Equal(1, jobClock.FailureOf(active.TenantId, activeJob)!.Failures);
    }

    [Fact]
    public async Task No_user_takes_the_clocks_steps_and_the_footer_lists_them_as_the_systems_judged_on_the_tenants_calendar()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock Hands-off Staffing");

        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        var job = await ConfirmedJobAsync(scenario, coordinator, "Running job", today.AddDays(-7), today.AddDays(60));
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var workflows = host.Get<WorkflowService>();
        var jobs = host.Get<JobService>();
        var refusal = WorkflowService.SystemDrivenRefusal("Period started");

        Assert.Equal(refusal, (await workflows.TransitionEntityAsync<JobState, Job>(job, JobWorkflowAttribute.PeriodStartsStep, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
        Assert.Equal(refusal, (await workflows.TransitionEntityToStateAsync<JobState, Job>(job, JobState.InProgress, JobWorkflowQueries.WithWorkflowGraph, cancellationToken: Ct)).Error);
        Assert.Equal(refusal, (await jobs.UpdateJobAndTransitionAsync((await jobs.LoadJobUpdaterAsync(job, Ct)).Value!, JobWorkflowAttribute.PeriodStartsStep, null, Ct)).Error);
        Assert.Equal(JobState.Confirmed, (await StoredJobAsync(job)).State);
        Assert.Equal(Confirmed, (await HistoryAsync(job)).Select(entry => entry.TransitionKey));

        var answers = await workflows.GetAvailableTransitionsAsync<JobState, Job>(job, JobWorkflowQueries.WithWorkflowGraph, Ct);
        Assert.True(answers.IsSuccess, answers.Error);
        var step = Assert.Single(answers.Value!, answer => answer.Transition.Key == JobWorkflowAttribute.PeriodStartsStep);
        Assert.Equal((true, refusal, false), (step.Transition.IsSystemDriven, step.AccessRefusal, step.CanRun));
        Assert.Empty(step.BlockingReasons);
        Assert.DoesNotContain(answers.Value!, answer => answer.Transition.Key == JobWorkflowAttribute.PeriodEndsStep);
    }

    [Fact]
    public async Task The_footers_answers_refuse_the_clocks_steps_to_every_session_as_running_them_does()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock Footer Staffing");
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        var job = await ConfirmedJobAsync(scenario, coordinator, "Offered job", today.AddDays(-7), today.AddDays(60));
        var impersonating = scenario.Onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(coordinator, scenario.TenantId);
        var refusal = WorkflowService.SystemDrivenRefusal("Period started");

        foreach (var runtime in new IRuntimeExecutionContext[] { scenario.Onboarded.OwnerRuntime(), scenario.Onboarded.RuntimeOf(coordinator), impersonating })
        {
            await using var host = ServiceHost.Create(this.database, runtime);
            var workflows = host.Get<WorkflowService>();
            var answers = await workflows.GetAvailableTransitionsAsync<JobState, Job>(job, JobWorkflowQueries.WithWorkflowGraph, Ct);
            Assert.True(answers.IsSuccess, answers.Error);
            Assert.Equal(refusal, Assert.Single(answers.Value!, answer => answer.Transition.Key == JobWorkflowAttribute.PeriodStartsStep).AccessRefusal);
            var gate = await workflows.GetTransitionAccessAsync<JobState, Job>(JobState.InProgress, Ct);
            Assert.True(gate.IsSuccess, gate.Error);
            Assert.Equal(WorkflowService.SystemDrivenRefusal("Period ended"), Assert.Single(gate.Value!, answer => answer.Transition.Key == JobWorkflowAttribute.PeriodEndsStep).AccessRefusal);
        }
    }

    [Fact(Timeout = 240000)]
    public async Task The_app_runs_the_clock_only_when_its_setting_switches_it_on()
    {
        var (scenario, coordinator) = await ArrangeAsync("Clock App Staffing");
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        var job = await ConfirmedJobAsync(scenario, coordinator, "App job", today.AddDays(-7), today.AddDays(60));

        await using (var off = AppHost.Start(this.database))
        {
            Assert.False(off.Services.GetRequiredService<IOptions<JobClockOptions>>().Value.Enabled);
            var service = Assert.Single(off.Services.GetServices<IHostedService>().OfType<JobClockService>());
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(JobState.Confirmed, (await StoredJobAsync(job)).State);
        }

        await using (var on = AppHost.Start(this.database.ConnectionString, "Test", new Dictionary<string, string> { ["JobClock:Enabled"] = "true" }))
        {
            Assert.True(on.Services.GetRequiredService<IOptions<JobClockOptions>>().Value.Enabled);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while ((await StoredJobAsync(job)).State != JobState.InProgress)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
        }

        var row = (await HistoryAsync(job))[^1];
        Assert.Equal((JobWorkflowAttribute.PeriodStartsStep, SystemAccount.IdFor(scenario.TenantId)), (row.TransitionKey, row.ActorId));
    }

    [Theory]
    [InlineData("00:00:00.5", false)]
    [InlineData("00:00:01", true)]
    [InlineData("01:00:00", true)]
    [InlineData("01:00:01", false)]
    public void The_clocks_interval_is_taken_from_a_second_to_an_hour_and_refused_outside_it(string interval, bool accepted)
    {
        var services = new ServiceCollection();
        services.AddJobClock(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JobClock:Interval"] = interval }).Build());
        using var provider = services.BuildServiceProvider();
        JobClockOptions Read() => provider.GetRequiredService<IOptions<JobClockOptions>>().Value;
        if (accepted)
        {
            Assert.Equal(TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture), Read().Interval);
        }
        else
        {
            Assert.Throws<OptionsValidationException>(Read);
        }
    }

    private async Task<(StaffScenario Scenario, Guid Coordinator)> ArrangeAsync(string name)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName(name));
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        return (scenario, coordinator);
    }

    private async Task<Guid> ConfirmedJobAsync(StaffScenario scenario, Guid coordinator, string name, DateOnly start, DateOnly end)
    {
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var job = await CoordinatingJobAsync(host, scenario, name, start, end);
        Assert.True((await StepAsync(host.Get<JobService>(), job, JobWorkflowAttribute.ConfirmStep)).IsSuccess);
        return job;
    }

    private async Task<Guid> CoordinatingJobAsync(ServiceHost host, StaffScenario scenario, string name, DateOnly start, DateOnly end)
    {
        var customers = host.Get<CustomerService>();
        var customer = await customers.CreateCustomerAsync(new Customer { Name = $"{name} customer {Guid.NewGuid():N}" }, CustomerWorkflowAttribute.ActivateStep, Ct);
        Assert.True(customer.IsSuccess, customer.Error);
        var jobs = host.Get<JobService>();
        var created = await jobs.CreateJobAsync(new Job
        {
            Name = name,
            CustomerId = customer.Value,
            OrganizationUnitId = scenario.RootId,
            PeriodStart = start,
            PeriodEnd = end,
            TimeSlots = [new JobTimeSlot { Weekday = Weekday.Monday, Start = new TimeOnly(8, 0), End = new TimeOnly(9, 0) }],
        }, JobWorkflowAttribute.RegisterStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        var slot = await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = created.Value, Name = "Lead" }, JobSlotWorkflowAttribute.AddStep, Ct);
        Assert.True(slot.IsSuccess, slot.Error);
        Assert.True((await StepAsync(jobs, created.Value, JobWorkflowAttribute.StartFromRegistrationStep)).IsSuccess);
        return created.Value;
    }

    private async Task<Result> StepAsync(JobService jobs, Guid job, string step)
    {
        var updater = await jobs.LoadJobUpdaterAsync(job, Ct);
        return updater.IsSuccess ? await jobs.UpdateJobAndTransitionAsync(updater.Value!, step, null, Ct) : Result.Failure(updater.Error);
    }

    private ServiceHost ClockHost(FakeClock clock, Action<IServiceCollection>? configure = null) =>
        ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
            configure?.Invoke(services);
        });

    private async Task<Job> StoredJobAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Jobs.AsNoTracking().SingleAsync(row => row.Id == id, Ct);
    }

    private async Task<List<StateHistoryEntry>> HistoryAsync(Guid job)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityType == nameof(Job) && entry.EntityId == job).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(Ct);
    }

    private async Task<string> TenantWritesAsync(Guid tenantId)
    {
        await using var context = this.database.CreateSystemContext();
        var history = await context.StateHistoryEntries.CountAsync(entry => entry.TenantId == tenantId, Ct);
        var jobs = await context.Jobs.AsNoTracking().Where(job => job.TenantId == tenantId).OrderBy(job => job.Id).Select(job => $"{job.Id}:{job.State}:{job.Modified:o}:{job.ModifiedById}").ToListAsync(Ct);
        return $"{history} history rows; {string.Join("; ", jobs)}";
    }

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    private sealed class FailsFor : IStepPreparation
    {
        private readonly Guid jobId;

        public FailsFor(Guid jobId)
        {
            this.jobId = jobId;
        }

        public Type EntityType => typeof(Job);

        public Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken) =>
            entityId == this.jobId ? throw new InvalidOperationException("The test broke this job's step.") : Task.CompletedTask;
    }

    private sealed class Breakable : IStepPreparation
    {
        private readonly Guid jobId;

        public Breakable(Guid jobId)
        {
            this.jobId = jobId;
        }

        public bool Broken { get; set; } = true;

        public Type EntityType => typeof(Job);

        public Task PrepareAsync(ApplicationDbContext context, Guid entityId, CancellationToken cancellationToken) =>
            Broken && entityId == this.jobId ? throw new InvalidOperationException("The test broke this job's step.") : Task.CompletedTask;
    }

    private sealed class MessageRecorder : ILoggerProvider, ILogger
    {
        private readonly List<string> messages;

        public MessageRecorder(List<string> messages)
        {
            this.messages = messages;
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (this.messages)
            {
                this.messages.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
