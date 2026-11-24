using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Staffora.Core.Annotations;
using Staffora.Core.Workflow;

namespace Staffora.Test.Unit.Workflow.Samples;

public enum TidyState
{
    [Display(Name = "Draft")]
    [Description("Being prepared")]
    [Icon("edit_note")]
    [StateStyling("#607D8B")]
    [EntityStateCategory(StateCategory.Entry)]
    Draft = 0,

    [Display(Name = "Done")]
    [Description("Finished")]
    [Icon("task_alt")]
    [StateStyling("#4CAF50")]
    [EntityStateCategory(StateCategory.Done)]
    Done = 1,
}

[TidyWorkflow]
public sealed class TidyEntity : StateEntityBase<TidyState>;

public sealed class TidyWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TidyState, TidyEntity>();
        workflow.AddState(TidyState.Draft);
        workflow.AddState(TidyState.Done);
        workflow.AddTransition(new("tidy-finish", TidyState.Draft, TidyState.Done) { Description = "Finish it" });
        return workflow;
    }
}

public enum UntidyState
{
    [Display(Name = "Open")]
    [Description("Open")]
    [Icon("radio_button_unchecked")]
    [StateStyling("#2196F3")]
    [EntityStateCategory(StateCategory.Open)]
    Open = 0,

    Closed = 1,

    [Display(ShortName = "Gone")]
    [Description(" ")]
    [Icon("archive")]
    [StateStyling("#9E9E9E")]
    [EntityStateCategory(StateCategory.Archived)]
    Gone = 2,
}

[UntidyWorkflow]
public sealed class UntidyEntity : StateEntityBase<UntidyState>;

public sealed class UntidyWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<UntidyState, UntidyEntity>();
        workflow.AddState(UntidyState.Open);
        workflow.AddState(UntidyState.Closed);
        workflow.AddState(UntidyState.Gone);
        workflow.AddTransition(new("untidy-close", UntidyState.Open, UntidyState.Closed) { Description = "Close it" });
        workflow.AddTransition(new("untidy-drop", UntidyState.Open, UntidyState.Gone));
        return workflow;
    }
}
