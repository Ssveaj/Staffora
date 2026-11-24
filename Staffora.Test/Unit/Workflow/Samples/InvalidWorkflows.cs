using System.ComponentModel.DataAnnotations.Schema;
using Staffora.Core.Annotations;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;

namespace Staffora.Test.Unit.Workflow.Samples;

public enum BrokenState
{
    Open = 0,
    Closed = 1,
}

[CascadeWithoutEdgeWorkflow]
public sealed class CascadeWithoutEdgeEntity : StateEntityBase<BrokenState>
{
    public List<SampleChild> Children { get; set; } = [];
}

public sealed class CascadeWithoutEdgeWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, CascadeWithoutEdgeEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed).Cascade(e => e.Children, ChildState.Imported);
        workflow.AddTransition(new("broken-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}

[CascadeWithEdgeWorkflow]
public sealed class CascadeWithEdgeEntity : StateEntityBase<BrokenState>
{
    public List<SampleChild> Children { get; set; } = [];
}

public sealed class CascadeWithEdgeWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, CascadeWithEdgeEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed).Cascade(e => e.Children, ChildState.Canceled, ChildState.Declined);
        workflow.AddTransition(new("broken-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}

[DeleteCascadeLeavingAloneWorkflow]
public sealed class DeleteCascadeLeavingAloneEntity : StateEntityBase<BrokenState>
{
    public List<SampleChild> Children { get; set; } = [];
}

public sealed class DeleteCascadeLeavingAloneWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, DeleteCascadeLeavingAloneEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed).Cascade(e => e.Children, ChildState.Deleted, ChildState.Declined);
        workflow.AddTransition(new("broken-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}

[MissingStateWorkflow]
public sealed class MissingStateEntity : StateEntityBase<BrokenState>;

public sealed class MissingStateWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, MissingStateEntity>();
        workflow.AddState(BrokenState.Open);
        return workflow;
    }
}

[UnknownReadWorkflow]
public sealed class UnknownReadEntity : StateEntityBase<BrokenState>;

public sealed class UnknownReadWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, UnknownReadEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed);
        workflow.AddTransition(new("broken-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" })
            .AddCondition(new DelegateCondition<BrokenState, UnknownReadEntity>("Has times", _ => true, "Times"));
        return workflow;
    }
}

[ComplexType]
public sealed class SampleAddress
{
    public string? Street { get; set; }
}

[ColumnReadWorkflow]
public sealed class ColumnReadEntity : StateEntityBase<BrokenState>
{
    public string? Name { get; set; }

    public List<string> Tags { get; set; } = [];

    public SampleAddress? Address { get; set; }
}

public sealed class ColumnReadWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, ColumnReadEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed);
        workflow.AddTransition(new("broken-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" })
            .AddCondition(new DelegateCondition<BrokenState, ColumnReadEntity>(
                "Has a name, tags and an address", _ => true, nameof(ColumnReadEntity.Name), nameof(ColumnReadEntity.Tags), nameof(ColumnReadEntity.Address)));
        return workflow;
    }
}

[CountedWorkflow]
public sealed class CountedEntity : StateEntityBase<BrokenState>;

public sealed class CountedWorkflowAttribute : WorkflowAttribute
{
    private static readonly AsyncLocal<BuildProbe?> probe = new();

    public static BuildProbe StartProbe()
    {
        var probe = new BuildProbe(() => CountedWorkflowAttribute.probe.Value = null);
        CountedWorkflowAttribute.probe.Value = probe;
        return probe;
    }

    public override IWorkflow CreateWorkflow()
    {
        probe.Value?.Built();

        var workflow = new Workflow<BrokenState, CountedEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed);
        workflow.AddTransition(new("counted-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}

public sealed class BuildProbe : IDisposable
{
    private int builds;
    private readonly Action end;

    public BuildProbe(Action end)
    {
        this.end = end;
    }

    public int Builds => Volatile.Read(ref this.builds);

    public ManualResetEventSlim Gate { get; } = new(initialState: true);

    internal void Built()
    {
        Interlocked.Increment(ref this.builds);
        Gate.Wait(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        Gate.Set();
        this.end();
    }
}

[ConfirmedAndPromptedWorkflow]
public sealed class ConfirmedAndPromptedEntity : StateEntityBase<BrokenState>;

public sealed class ConfirmedAndPromptedWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, ConfirmedAndPromptedEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed);
        workflow.AddTransition(new("broken-close", BrokenState.Open, BrokenState.Closed)
        {
            Description = "Close",
            ConfirmationMessage = "Close it?",
            CommentPrompt = new("Close it") { Message = "Why?" },
        });
        return workflow;
    }
}

[UncommentedPromptWorkflow]
public sealed class UncommentedPromptEntity : TenantStateEntityBase<BrokenState>;

public sealed class UncommentedPromptWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, UncommentedPromptEntity>();
        workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed);
        workflow.AddTransition(new("broken-close", BrokenState.Open, BrokenState.Closed)
        {
            Description = "Close",
            CommentPrompt = new("Close it") { Message = "Why?" },
        });
        return workflow;
    }
}

public enum PromptedDeleteState
{
    [EntryState]
    Open = 0,

    [DeleteState]
    Deleted = 1,
}

[PromptedDeleteWorkflow]
public sealed class PromptedDeleteEntity : TenantStateEntityBase<PromptedDeleteState>, ICommentedEntity;

public sealed class PromptedDeleteWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<PromptedDeleteState, PromptedDeleteEntity>();
        workflow.AddState(PromptedDeleteState.Open);
        workflow.AddState(PromptedDeleteState.Deleted);
        workflow.AddTransition(new("broken-delete", PromptedDeleteState.Open, PromptedDeleteState.Deleted)
        {
            Description = "Delete",
            CommentPrompt = new("Delete it") { Message = "Why?" },
        });
        return workflow;
    }
}

public enum InvalidSave
{
    TwoInOneState = 0,

    LeavesItsState = 1,

    AsksAConfirmation = 2,

    AsksAComment = 3,

    RunsAnAction = 4,

    StateRunsAnAction = 5,

    SystemDriven = 6,

    PlatformOwnerOnly = 7,

    StateRunsALeaveAction = 8,

    TakesNoEdits = 9,
}

public sealed class InvalidSaveWorkflowAttribute<TEntity> : WorkflowAttribute
    where TEntity : class, IStateEntity<BrokenState>
{
    private readonly InvalidSave mistake;

    public InvalidSaveWorkflowAttribute(InvalidSave mistake)
    {
        this.mistake = mistake;
    }

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, TEntity>();
        var open = workflow.AddState(BrokenState.Open);
        workflow.AddState(BrokenState.Closed);
        if (this.mistake == InvalidSave.StateRunsAnAction)
        {
            open.AddEnterAction(new Staffora.Core.Workflow.Actions.DelegateAction<BrokenState, TEntity>(_ => { }));
        }

        if (this.mistake == InvalidSave.StateRunsALeaveAction)
        {
            open.AddLeaveAction(new Staffora.Core.Workflow.Actions.DelegateAction<BrokenState, TEntity>(_ => { }));
        }

        var save = workflow.AddTransition(new("broken-save", BrokenState.Open, this.mistake == InvalidSave.LeavesItsState ? BrokenState.Closed : BrokenState.Open)
        {
            Description = "Save",
            IsSave = true,
            ConfirmationMessage = this.mistake == InvalidSave.AsksAConfirmation ? "Save it?" : null,
            CommentPrompt = this.mistake == InvalidSave.AsksAComment ? new("Save it") { Message = "Why?" } : null,
            IsSystemDriven = this.mistake == InvalidSave.SystemDriven,
            RequiresPlatformOwner = this.mistake == InvalidSave.PlatformOwnerOnly,
            TakesNoEdits = this.mistake == InvalidSave.TakesNoEdits,
        });
        if (this.mistake == InvalidSave.RunsAnAction)
        {
            save.AddAction(new Staffora.Core.Workflow.Actions.DelegateAction<BrokenState, TEntity>(_ => { }));
        }

        if (this.mistake == InvalidSave.TwoInOneState)
        {
            workflow.AddTransition(new("broken-save-again", BrokenState.Open, BrokenState.Open) { Description = "Save again", IsSave = true });
        }

        return workflow;
    }
}

[InvalidSaveWorkflow<TwoSavesEntity>(InvalidSave.TwoInOneState)]
public sealed class TwoSavesEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<LeavingSaveEntity>(InvalidSave.LeavesItsState)]
public sealed class LeavingSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<ConfirmedSaveEntity>(InvalidSave.AsksAConfirmation)]
public sealed class ConfirmedSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<PromptedSaveEntity>(InvalidSave.AsksAComment)]
public sealed class PromptedSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<ActingSaveEntity>(InvalidSave.RunsAnAction)]
public sealed class ActingSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<EnteringSaveEntity>(InvalidSave.StateRunsAnAction)]
public sealed class EnteringSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<ExitingSaveEntity>(InvalidSave.StateRunsALeaveAction)]
public sealed class ExitingSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<SystemSaveEntity>(InvalidSave.SystemDriven)]
public sealed class SystemSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<OwnerSaveEntity>(InvalidSave.PlatformOwnerOnly)]
public sealed class OwnerSaveEntity : StateEntityBase<BrokenState>;

[InvalidSaveWorkflow<NoEditsSaveEntity>(InvalidSave.TakesNoEdits)]
public sealed class NoEditsSaveEntity : StateEntityBase<BrokenState>;

[SiblingDeleteWorkflow]
public sealed class SiblingDeleteEntity : StateEntityBase<LinkState>
{
    public SiblingDeleteEntity? Parent { get; set; }

    public List<SiblingDeleteEntity> Siblings { get; set; } = [];
}

public sealed class SiblingDeleteWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<LinkState, SiblingDeleteEntity>();
        workflow.AddState(LinkState.Open);
        workflow.AddState(LinkState.Deleted).Cascade(e => e.Parent!.Siblings, LinkState.Deleted);
        workflow.AddTransition(new("sibling-delete", LinkState.Open, LinkState.Deleted) { Description = "Delete it and its siblings" });
        return workflow;
    }
}
