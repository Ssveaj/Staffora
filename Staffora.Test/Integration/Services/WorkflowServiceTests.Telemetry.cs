using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Services.Diagnostics;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public partial class WorkflowServiceTests
{
    [Fact]
    public async Task A_workflow_step_runs_in_an_activity_tagged_with_its_entity_step_and_outcome()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var steps = new StepActivities();
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();

        Assert.True((await service.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken)).IsSuccess);
        Assert.False((await service.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken)).IsSuccess);

        var taken = steps.Of(id);
        Assert.Equal(2, taken.Count);
        Assert.All(taken, activity =>
        {
            Assert.Equal(StafforaTelemetry.WorkflowStepActivity, activity.OperationName);
            Assert.Equal(StafforaTelemetry.Name, activity.Source.Name);
            Assert.Equal("TestParent test-parent-activate", activity.DisplayName);
            Assert.Equal(nameof(TestParent), activity.GetTagItem(StafforaTelemetry.EntityTypeTag));
            Assert.Equal("test-parent-activate", activity.GetTagItem(StafforaTelemetry.StepTag));
            Assert.Equal(false, activity.GetTagItem(StafforaTelemetry.SystemStepTag));
        });
        Assert.Equal([StafforaTelemetry.Done, StafforaTelemetry.Refused], taken.Select(activity => activity.GetTagItem(StafforaTelemetry.OutcomeTag)));
        Assert.All(taken.SelectMany(activity => activity.TagObjects), tag => Assert.StartsWith("staffora.", tag.Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_system_step_inside_another_write_runs_in_its_own_activity_marked_as_the_systems()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var steps = new StepActivities();
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Canceled, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());

        await using (var context = host.Get<IApplicationDbContextFactory<ApplicationDbContext>>().CreateDbContext(scenario.A.Runtime()))
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            var parent = await context.Set<TestParent>().FindAsync([id], cancellationToken);
            await host.Get<WorkflowService>().RunSystemStepInTransactionAsync<TestParentState, TestParent>(context, parent!, "test-parent-restore", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var step = Assert.Single(steps.Of(id));
        Assert.Equal(("test-parent-restore", true, StafforaTelemetry.Done), (step.GetTagItem(StafforaTelemetry.StepTag), step.GetTagItem(StafforaTelemetry.SystemStepTag), step.GetTagItem(StafforaTelemetry.OutcomeTag)));
    }

    [Fact]
    public async Task An_error_logged_inside_a_workflow_step_carries_the_steps_trace_id_as_its_correlation_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var steps = new StepActivities();
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, cancellationToken, notes: TestParent.FailInACondition);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services =>
            services.AddSingleton<ILoggerProvider>(provider => new ErrorLogLoggerProvider(provider.GetRequiredService<ErrorLogQueue>(), TimeProvider.System)));

        Assert.False((await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken)).IsSuccess);

        var step = Assert.Single(steps.Of(id));
        Assert.Equal(StafforaTelemetry.Refused, step.GetTagItem(StafforaTelemetry.OutcomeTag));
        var queue = host.Get<ErrorLogQueue>();
        var errors = new List<ErrorLog>();
        while (queue.Reader.TryRead(out var queued))
        {
            errors.Add(queued.Error);
        }

        var error = Assert.Single(errors, row => row.Message.Contains(id.ToString(), StringComparison.Ordinal));
        Assert.Equal(step.TraceId.ToHexString(), error.CorrelationId);
    }

    private sealed class StepActivities : IDisposable
    {
        private readonly ConcurrentQueue<Activity> stopped = new();
        private readonly ActivityListener listener;

        public StepActivities()
        {
            this.listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == StafforaTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = this.stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(this.listener);
        }

        public List<Activity> Of(Guid entityId) =>
            [.. this.stopped.Where(activity => activity.OperationName == StafforaTelemetry.WorkflowStepActivity && Equals(activity.GetTagItem(StafforaTelemetry.EntityIdTag), entityId.ToString())).OrderBy(activity => activity.StartTimeUtc)];

        public void Dispose() => this.listener.Dispose();
    }
}
