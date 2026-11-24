using Staffora.Core.Entities.Messaging;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Actions;

namespace Staffora.Test.Unit.Workflow.Samples;

public enum ProbeState
{
    Start = 0,
    End = 1,
}

[ProbeWorkflow]
public sealed class ProbeEntity : StateEntityBase<ProbeState>
{
    public List<string> Log { get; } = [];

    public SampleGrandchild TouchedOnLeave { get; } = new() { Id = Guid.NewGuid() };

    public SampleGrandchild TouchedByTransition { get; } = new() { Id = Guid.NewGuid() };

    public SampleGrandchild TouchedOnEnter { get; } = new() { Id = Guid.NewGuid() };

    public bool AfterCommitRan { get; set; }
}

public sealed class ProbeWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<ProbeState, ProbeEntity>();

        workflow.AddState(ProbeState.Start)
            .AddLeaveAction(new DelegateAction<ProbeState, ProbeEntity>(c => Write(c, "leave"), c => [c.Entity.TouchedOnLeave]));

        workflow.AddState(ProbeState.End)
            .AddEnterAction(new DelegateAction<ProbeState, ProbeEntity>(c => Write(c, "enter"), c => [c.Entity.TouchedOnEnter]));

        workflow.AddTransition(new("probe-run", ProbeState.Start, ProbeState.End) { Description = "Run the probe" })
            .AddAction(new DelegateAction<ProbeState, ProbeEntity>(
                c =>
                {
                    Write(c, "transition");
                    c.OutgoingMessages.Add(new OutgoingMessage { MessageType = "ProbeRan", EntityId = c.Entity.Id, RecipientUserIds = [Guid.NewGuid()] });
                    c.AfterCommit.Add(_ =>
                    {
                        c.Entity.AfterCommitRan = true;
                        return Task.CompletedTask;
                    });
                },
                c => [c.Entity.TouchedByTransition]));

        return workflow;
    }

    private static void Write(WorkflowContext<ProbeState, ProbeEntity> context, string step) =>
        context.Entity.Log.Add($"{step}: state={context.Entity.State} target={context.TargetState} inTransition={context.Entity.IsInTransition} comment={context.Comment}");
}
