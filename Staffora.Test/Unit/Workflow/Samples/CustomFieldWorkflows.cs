using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Test.Unit.Workflow.Samples;

[WellFormedAttributesWorkflow]
public sealed class WellFormedAttributesEntity : StateEntityBase<BrokenState>
{
    public ICollection<WellFormedAttributesValue> Attributes { get; set; } = [];
}

public sealed class WellFormedAttributesDefinition : EntityAttributeDefinitionBase<WellFormedAttributesEntity>;

public sealed class WellFormedAttributesValue : EntityAttributeBase<WellFormedAttributesEntity, WellFormedAttributesDefinition>;

public sealed class WellFormedAttributesWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, WellFormedAttributesEntity>();
        workflow.AddState(BrokenState.Open).ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes)));
        workflow.AddState(BrokenState.Closed).ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes)));
        workflow.AddTransition(new("well-formed-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}

[MissingAttributesAreaWorkflow]
public sealed class MissingAttributesAreaEntity : StateEntityBase<BrokenState>
{
    public ICollection<MissingAttributesAreaValue> Attributes { get; set; } = [];
}

public sealed class MissingAttributesAreaDefinition : EntityAttributeDefinitionBase<MissingAttributesAreaEntity>;

public sealed class MissingAttributesAreaValue : EntityAttributeBase<MissingAttributesAreaEntity, MissingAttributesAreaDefinition>;

public sealed class MissingAttributesAreaWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, MissingAttributesAreaEntity>();
        workflow.AddState(BrokenState.Open).ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes)));
        workflow.AddState(BrokenState.Closed).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddTransition(new("missing-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}

[OverriddenAttributesWorkflow]
public sealed class OverriddenAttributesEntity : StateEntityBase<BrokenState>
{
    public ICollection<OverriddenAttributesValue> Attributes { get; set; } = [];
}

public sealed class OverriddenAttributesDefinition : EntityAttributeDefinitionBase<OverriddenAttributesEntity>;

public sealed class OverriddenAttributesValue : EntityAttributeBase<OverriddenAttributesEntity, OverriddenAttributesDefinition>;

public sealed class OverriddenAttributesWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, OverriddenAttributesEntity>();
        workflow.AddState(BrokenState.Open).ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes)).Field(e => e.Attributes, FieldAccessFlags.ReadWrite));
        workflow.AddState(BrokenState.Closed).ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes)));
        workflow.AddTransition(new("overridden-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}

[MandatoryAttributesWorkflow]
public sealed class MandatoryAttributesEntity : StateEntityBase<BrokenState>
{
    public ICollection<MandatoryAttributesValue> Attributes { get; set; } = [];
}

public sealed class MandatoryAttributesDefinition : EntityAttributeDefinitionBase<MandatoryAttributesEntity>;

public sealed class MandatoryAttributesValue : EntityAttributeBase<MandatoryAttributesEntity, MandatoryAttributesDefinition>;

public sealed class MandatoryAttributesWorkflowAttribute : WorkflowAttribute
{
    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<BrokenState, MandatoryAttributesEntity>();
        workflow.AddState(BrokenState.Open).ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.Required, a => a.Field(e => e.Attributes)));
        workflow.AddState(BrokenState.Closed).ConfigureFields(f => f.Area(WorkflowAreas.Attributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes)));
        workflow.AddTransition(new("mandatory-close", BrokenState.Open, BrokenState.Closed) { Description = "Close" });
        return workflow;
    }
}
