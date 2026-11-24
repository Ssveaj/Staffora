using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class FieldAccessTests
{
    [Fact]
    public void Field_override_wins_over_its_area_and_area_over_the_state_default()
    {
        var active = Environments.Parent.GetFieldConfig(ParentState.Active);

        Assert.Equal(FieldAccessFlags.Required, active.GetFieldAccess(e => e.Notes));
        Assert.Equal(FieldAccessFlags.ReadWrite, active.GetFieldAccess(e => e.Children));
        Assert.Equal(FieldAccessFlags.ReadOnly, active.GetFieldAccess(e => e.Name));
        Assert.Equal(SampleParentWorkflowAttribute.AreaChildren, active.GetArea(nameof(SampleParent.Children)));
    }

    [Fact]
    public void Areas_resolve_to_their_own_flags_and_an_unconfigured_area_follows_the_state_default()
    {
        var active = Environments.Parent.GetFieldConfig(ParentState.Active);

        Assert.Equal(FieldAccessFlags.None, active.GetAreaAccess(SampleParentWorkflowAttribute.AreaAttributes));
        Assert.Equal(FieldAccessFlags.ReadWrite, active.GetAreaAccess(SampleParentWorkflowAttribute.AreaChildren));
        Assert.Equal(FieldAccessFlags.ReadOnly, active.GetAreaAccess("Unconfigured"));
    }

    [Fact]
    public void A_state_without_a_default_is_ReadWrite()
    {
        var draft = Environments.Parent.GetFieldConfig(ParentState.Draft);

        Assert.Equal(FieldAccessFlags.ReadWrite, draft.DefaultAccess);
        Assert.Equal(FieldAccessFlags.ReadWrite, draft.GetFieldAccess(e => e.Notes));
        Assert.True(draft.IsMandatory(e => e.Name));
    }

    [Fact]
    public void Field_access_follows_the_entitys_current_state()
    {
        var parent = new SampleParent { State = ParentState.Deleted };

        var config = Environments.Parent.GetFieldConfig(parent);

        Assert.False(config.IsVisible(e => e.Name));
        Assert.False(config.IsEditable(e => e.Notes));
    }

    [Fact]
    public void The_untyped_workflow_resolves_field_access_like_the_typed_config_and_refuses_a_foreign_state()
    {
        IWorkflow workflow = Environments.Parent;
        var active = Environments.Parent.GetFieldConfig(ParentState.Active);

        Assert.Equal(active.GetFieldAccess(e => e.Notes), workflow.GetFieldAccess(ParentState.Active, nameof(SampleParent.Notes)));
        Assert.Equal(active.GetFieldAccess(e => e.Name), workflow.GetFieldAccess(ParentState.Active, nameof(SampleParent.Name)));
        Assert.Throws<ArgumentException>(() => workflow.GetFieldAccess(ProbeState.Start, nameof(SampleParent.Name)));
    }

    [Fact]
    public void A_field_is_declared_by_an_override_or_an_area_in_some_state_and_not_by_a_state_default()
    {
        IWorkflow workflow = Environments.Parent;
        var active = Environments.Parent.GetFieldConfig(ParentState.Active);
        var canceled = Environments.Parent.GetFieldConfig(ParentState.Canceled);

        Assert.True(active.Declares(nameof(SampleParent.Notes)));
        Assert.True(active.Declares(nameof(SampleParent.Children)));
        Assert.False(active.Declares(nameof(SampleParent.Name)));
        Assert.False(canceled.Declares(nameof(SampleParent.Children)));

        Assert.True(workflow.DeclaresField(nameof(SampleParent.Children)));
        Assert.True(workflow.DeclaresField(nameof(SampleParent.Name)));
        Assert.False(workflow.DeclaresField(nameof(SampleParent.State)));
    }

    [Fact]
    public void A_field_in_two_areas_of_one_state_is_refused()
    {
        var workflow = new Workflow<ParentState, SampleParent>();

        Assert.Throws<WorkflowDefinitionException>(() => workflow.AddState(ParentState.Draft).ConfigureFields(f => f
            .Area("First", FieldAccessFlags.ReadOnly, a => a.Field(e => e.Notes))
            .Area("Second", FieldAccessFlags.ReadWrite, a => a.Field(e => e.Notes))));
    }

    [Fact]
    public void A_selector_must_name_a_property_of_the_entity()
    {
        var workflow = new Workflow<ParentState, SampleParent>();

        Assert.Throws<ArgumentException>(() => workflow.AddState(ParentState.Draft).ConfigureFields(f => f
            .Field(e => e.Children.Count, FieldAccessFlags.ReadOnly)));
    }
}
