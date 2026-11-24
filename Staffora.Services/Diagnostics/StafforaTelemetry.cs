using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Staffora.Services.Diagnostics;

public sealed class StafforaTelemetry
{
    public const string Name = "Staffora";

    public const string WorkflowStepActivity = "staffora.workflow.step";
    public const string OutboxPassActivity = "staffora.outbox.pass";
    public const string OutboxDispatchActivity = "staffora.outbox.dispatch";
    public const string JobClockPassActivity = "staffora.job_clock.pass";
    public const string RetentionPassActivity = "staffora.retention.pass";

    public const string WorkflowSteps = "staffora.workflow.steps";
    public const string WorkflowStepDuration = "staffora.workflow.step.duration";
    public const string OutboxPending = "staffora.outbox.pending";
    public const string OutboxDeadLetters = "staffora.outbox.dead_letters";
    public const string OutboxDispatchDuration = "staffora.outbox.dispatch.duration";
    public const string JobClockPasses = "staffora.job_clock.passes";
    public const string JobClockSteps = "staffora.job_clock.steps";
    public const string RetentionRowsRemoved = "staffora.retention.rows_removed";

    public const string EntityTypeTag = "staffora.entity.type";
    public const string EntityIdTag = "staffora.entity.id";
    public const string StepTag = "staffora.step";
    public const string SystemStepTag = "staffora.step.system";
    public const string OutcomeTag = "staffora.outcome";
    public const string MessageKindTag = "staffora.outbox.kind";
    public const string MessageIdTag = "staffora.outbox.message.id";
    public const string RetentionRuleTag = "staffora.retention.rule";

    public const string Done = "done";
    public const string Refused = "refused";
    public const string Failed = "failed";
    public const string NoStep = "none";

    public static readonly ActivitySource Source = new(Name);

    private readonly TimeProvider clock;
    private readonly Counter<long> workflowSteps;
    private readonly Histogram<double> workflowStepDuration;
    private readonly Gauge<long> outboxPending;
    private readonly Gauge<long> outboxDeadLetters;
    private readonly Histogram<double> outboxDispatchDuration;
    private readonly Counter<long> jobClockPasses;
    private readonly Counter<long> jobClockSteps;
    private readonly Counter<long> retentionRowsRemoved;

    public StafforaTelemetry(IMeterFactory meters, TimeProvider clock)
    {
        this.clock = clock;
        Meter = meters.Create(Name);
        this.workflowSteps = Meter.CreateCounter<long>(WorkflowSteps, "{step}", "Workflow steps taken, by entity type, step and outcome.");
        this.workflowStepDuration = Meter.CreateHistogram<double>(WorkflowStepDuration, "s", "How long a workflow step took, by entity type, step and outcome.");
        this.outboxPending = Meter.CreateGauge<long>(OutboxPending, "{message}", "Integration messages waiting to be delivered, read at the end of each dispatcher pass.");
        this.outboxDeadLetters = Meter.CreateGauge<long>(OutboxDeadLetters, "{message}", "Integration messages given up as dead letters, read at the end of each dispatcher pass.");
        this.outboxDispatchDuration = Meter.CreateHistogram<double>(OutboxDispatchDuration, "s", "How long the dispatcher took over one integration message, by kind and outcome.");
        this.jobClockPasses = Meter.CreateCounter<long>(JobClockPasses, "{pass}", "Passes of the job clock.");
        this.jobClockSteps = Meter.CreateCounter<long>(JobClockSteps, "{step}", "Steps the job clock took on jobs, by step.");
        this.retentionRowsRemoved = Meter.CreateCounter<long>(RetentionRowsRemoved, "{row}", "Rows the retention pass removed, by rule.");
    }

    public Meter Meter { get; }

    public bool ReadsOutboxBacklog => this.outboxPending.Enabled || this.outboxDeadLetters.Enabled;

    public WorkflowStepTelemetry StartWorkflowStep(string entityType, Guid? entityId, string? stepKey, bool systemStep)
    {
        var activity = Source.StartActivity(WorkflowStepActivity, ActivityKind.Internal);
        activity?.SetTag(EntityTypeTag, entityType);
        activity?.SetTag(SystemStepTag, systemStep);
        if (entityId is { } id && id != Guid.Empty)
        {
            activity?.SetTag(EntityIdTag, id.ToString());
        }

        return new WorkflowStepTelemetry(this, activity, entityType, stepKey, this.clock.GetTimestamp());
    }

    public long Timestamp() => this.clock.GetTimestamp();

    public void RecordOutboxBacklog(long pending, long deadLetters)
    {
        this.outboxPending.Record(pending);
        this.outboxDeadLetters.Record(deadLetters);
    }

    public void RecordOutboxDispatch(string kind, string outcome, long startedAt) =>
        this.outboxDispatchDuration.Record(Seconds(startedAt), new(MessageKindTag, kind), new(OutcomeTag, outcome));

    public void RecordJobClockPass(IReadOnlyDictionary<string, int> stepsTaken)
    {
        this.jobClockPasses.Add(1);
        foreach (var (step, count) in stepsTaken.Where(taken => taken.Value > 0))
        {
            this.jobClockSteps.Add(count, new KeyValuePair<string, object?>(StepTag, step));
        }
    }

    public void RecordRetentionRemoved(string rule, long rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        if (rows > 0)
        {
            this.retentionRowsRemoved.Add(rows, new KeyValuePair<string, object?>(RetentionRuleTag, rule));
        }
    }

    internal void RecordWorkflowStep(string entityType, string stepKey, string outcome, long startedAt)
    {
        var tags = new TagList { { EntityTypeTag, entityType }, { StepTag, stepKey }, { OutcomeTag, outcome } };
        this.workflowSteps.Add(1, tags);
        this.workflowStepDuration.Record(Seconds(startedAt), tags);
    }

    private double Seconds(long startedAt) => this.clock.GetElapsedTime(startedAt).TotalSeconds;
}

public sealed class WorkflowStepTelemetry : IDisposable
{
    private readonly StafforaTelemetry telemetry;
    private readonly Activity? activity;
    private readonly string entityType;
    private readonly long startedAt;
    private string? stepKey;
    private string outcome = StafforaTelemetry.Failed;
    private bool disposed;

    internal WorkflowStepTelemetry(StafforaTelemetry telemetry, Activity? activity, string entityType, string? stepKey, long startedAt)
    {
        this.telemetry = telemetry;
        this.activity = activity;
        this.entityType = entityType;
        this.startedAt = startedAt;
        Picked(stepKey);
    }

    public void Picked(string? stepKey)
    {
        if (string.IsNullOrWhiteSpace(stepKey))
        {
            return;
        }

        this.stepKey = stepKey;
        this.activity?.SetTag(StafforaTelemetry.StepTag, stepKey);
        if (this.activity is not null)
        {
            this.activity.DisplayName = $"{this.entityType} {stepKey}";
        }
    }

    public void Finish(bool succeeded, Guid? entityId = null)
    {
        this.outcome = succeeded ? StafforaTelemetry.Done : StafforaTelemetry.Refused;
        if (entityId is { } id && id != Guid.Empty)
        {
            this.activity?.SetTag(StafforaTelemetry.EntityIdTag, id.ToString());
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.activity?.SetTag(StafforaTelemetry.OutcomeTag, this.outcome);
        if (this.outcome == StafforaTelemetry.Failed)
        {
            this.activity?.SetStatus(ActivityStatusCode.Error);
        }

        this.activity?.Dispose();
        this.telemetry.RecordWorkflowStep(this.entityType, this.stepKey ?? StafforaTelemetry.NoStep, this.outcome, this.startedAt);
    }
}
