using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Services.Diagnostics;

namespace Staffora.Test.Unit.Diagnostics;

public sealed class StafforaTelemetryTests : IDisposable
{
    private static readonly string[] instrumentNames =
    [
        StafforaTelemetry.WorkflowSteps,
        StafforaTelemetry.WorkflowStepDuration,
        StafforaTelemetry.OutboxPending,
        StafforaTelemetry.OutboxDeadLetters,
        StafforaTelemetry.OutboxDispatchDuration,
        StafforaTelemetry.JobClockPasses,
        StafforaTelemetry.JobClockSteps,
        StafforaTelemetry.RetentionRowsRemoved,
    ];

    private readonly ServiceProvider services;
    private readonly StepClock clock = new();
    private readonly StafforaTelemetry telemetry;
    private readonly MeterListener listener = new();
    private readonly List<Measurement> measurements = [];

    public StafforaTelemetryTests()
    {
        this.services = new ServiceCollection().AddSingleton<TimeProvider>(this.clock).AddStafforaTelemetry().BuildServiceProvider();
        this.telemetry = this.services.GetRequiredService<StafforaTelemetry>();
        this.listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, this.telemetry.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        this.listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        this.listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        this.listener.Start();
    }

    public void Dispose()
    {
        this.listener.Dispose();
        this.services.Dispose();
    }

    [Fact]
    public void The_meter_is_named_Staffora_and_carries_every_custom_instrument()
    {
        var published = new List<string>();
        using var names = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, this.telemetry.Meter))
                {
                    published.Add(instrument.Name);
                }
            },
        };
        names.Start();

        Assert.Equal(StafforaTelemetry.Name, this.telemetry.Meter.Name);
        Assert.Equal(instrumentNames.Order(StringComparer.Ordinal), published.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_workflow_step_counts_once_and_records_its_duration_by_entity_type_step_and_outcome()
    {
        using (var step = this.telemetry.StartWorkflowStep("Job", Guid.NewGuid(), stepKey: null, systemStep: false))
        {
            step.Picked("job-confirm");
            this.clock.Advance(TimeSpan.FromMilliseconds(250));
            step.Finish(succeeded: true);
        }

        var count = Assert.Single(Of(StafforaTelemetry.WorkflowSteps));
        var duration = Assert.Single(Of(StafforaTelemetry.WorkflowStepDuration));
        Assert.Equal(1L, count.Value);
        Assert.Equal(0.25, (double)duration.Value, 3);
        foreach (var measurement in new[] { count, duration })
        {
            Assert.Equal("Job", measurement.Tags[StafforaTelemetry.EntityTypeTag]);
            Assert.Equal("job-confirm", measurement.Tags[StafforaTelemetry.StepTag]);
            Assert.Equal(StafforaTelemetry.Done, measurement.Tags[StafforaTelemetry.OutcomeTag]);
            Assert.False(measurement.Tags.ContainsKey(StafforaTelemetry.EntityIdTag));
        }
    }

    [Fact]
    public void A_refused_step_and_one_that_never_finished_are_told_apart()
    {
        using (var refused = this.telemetry.StartWorkflowStep("Resource", null, "resource-activate", systemStep: true))
        {
            refused.Finish(succeeded: false);
        }

        using (this.telemetry.StartWorkflowStep("Resource", null, stepKey: null, systemStep: false))
        {
        }

        Assert.Equal(
            [(StafforaTelemetry.Refused, "resource-activate"), (StafforaTelemetry.Failed, StafforaTelemetry.NoStep)],
            Of(StafforaTelemetry.WorkflowSteps).Select(measurement => ((string)measurement.Tags[StafforaTelemetry.OutcomeTag]!, (string)measurement.Tags[StafforaTelemetry.StepTag]!)));
    }

    [Fact]
    public void The_outbox_backlog_is_read_as_two_gauges_and_each_dispatch_as_a_duration_by_kind_and_outcome()
    {
        Assert.True(this.telemetry.ReadsOutboxBacklog);

        this.telemetry.RecordOutboxBacklog(pending: 7, deadLetters: 2);
        var startedAt = this.telemetry.Timestamp();
        this.clock.Advance(TimeSpan.FromSeconds(2));
        this.telemetry.RecordOutboxDispatch("order.sent", "delivered", startedAt);

        Assert.Equal(7L, Assert.Single(Of(StafforaTelemetry.OutboxPending)).Value);
        Assert.Equal(2L, Assert.Single(Of(StafforaTelemetry.OutboxDeadLetters)).Value);
        var dispatch = Assert.Single(Of(StafforaTelemetry.OutboxDispatchDuration));
        Assert.Equal(2.0, (double)dispatch.Value, 3);
        Assert.Equal(("order.sent", "delivered"), ((string)dispatch.Tags[StafforaTelemetry.MessageKindTag]!, (string)dispatch.Tags[StafforaTelemetry.OutcomeTag]!));
    }

    [Fact]
    public void The_outbox_backlog_is_not_read_while_nobody_listens()
    {
        using var quiet = new ServiceCollection().AddSingleton<TimeProvider>(this.clock).AddStafforaTelemetry().BuildServiceProvider();

        Assert.False(quiet.GetRequiredService<StafforaTelemetry>().ReadsOutboxBacklog);
    }

    [Fact]
    public void A_job_clock_pass_counts_once_and_its_steps_by_step_leaving_out_the_steps_it_did_not_take()
    {
        this.telemetry.RecordJobClockPass(new Dictionary<string, int> { ["job-period-starts"] = 3, ["job-period-ends"] = 0 });

        Assert.Equal(1L, Assert.Single(Of(StafforaTelemetry.JobClockPasses)).Value);
        var steps = Assert.Single(Of(StafforaTelemetry.JobClockSteps));
        Assert.Equal((3L, "job-period-starts"), ((long)steps.Value, (string)steps.Tags[StafforaTelemetry.StepTag]!));
    }

    [Fact]
    public void The_retention_pass_counts_the_rows_it_removed_by_rule_and_nothing_for_a_rule_that_removed_none()
    {
        this.telemetry.RecordRetentionRemoved("error-log", 120);
        this.telemetry.RecordRetentionRemoved("read-notices", 0);

        var removed = Assert.Single(Of(StafforaTelemetry.RetentionRowsRemoved));
        Assert.Equal((120L, "error-log"), ((long)removed.Value, (string)removed.Tags[StafforaTelemetry.RetentionRuleTag]!));
        Assert.Throws<ArgumentException>(() => this.telemetry.RecordRetentionRemoved(" ", 1));
    }

    [Fact]
    public void Every_tag_of_a_measurement_is_a_key_and_never_a_name_or_address()
    {
        using (var step = this.telemetry.StartWorkflowStep("User", Guid.NewGuid(), "user-deactivate", systemStep: false))
        {
            step.Finish(succeeded: true, Guid.NewGuid());
        }

        this.telemetry.RecordOutboxDispatch("order.sent", "failed", this.telemetry.Timestamp());

        var allowed = new[] { StafforaTelemetry.EntityTypeTag, StafforaTelemetry.StepTag, StafforaTelemetry.OutcomeTag, StafforaTelemetry.MessageKindTag };
        Assert.All(this.measurements.SelectMany(measurement => measurement.Tags.Keys), key => Assert.Contains(key, allowed));
    }

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, tag) in tags)
        {
            copy[key] = tag;
        }

        lock (this.measurements)
        {
            this.measurements.Add(new Measurement(instrument.Name, value, copy));
        }
    }

    private List<Measurement> Of(string name)
    {
        lock (this.measurements)
        {
            return [.. this.measurements.Where(measurement => measurement.Instrument == name)];
        }
    }

    private sealed record Measurement(string Instrument, object Value, Dictionary<string, object?> Tags);

    private sealed class StepClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref this.ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref this.ticks, by.Ticks);
    }
}
