using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class CustomFieldWorkflowTests
{
    [Fact]
    public void A_workflow_that_puts_its_values_in_the_Attributes_area_in_every_state_registers()
    {
        var workflow = new WorkflowRegistry().Get(typeof(WellFormedAttributesEntity));

        Assert.True(workflow.DeclaresField(nameof(WellFormedAttributesEntity.Attributes)));
        Assert.Equal(Staffora.Core.Workflow.FieldAccess.FieldAccessFlags.ReadOnly, workflow.GetFieldAccess(BrokenState.Closed, nameof(WellFormedAttributesEntity.Attributes)));
    }

    [Theory]
    [InlineData(typeof(MissingAttributesAreaEntity), "MissingAttributesAreaEntity.Closed does not put Attributes in the Attributes area")]
    [InlineData(typeof(OverriddenAttributesEntity), "OverriddenAttributesEntity.Open does not put Attributes in the Attributes area")]
    [InlineData(typeof(MandatoryAttributesEntity), "MandatoryAttributesEntity.Open makes the Attributes area Mandatory")]
    public void A_workflow_whose_custom_fields_do_not_follow_one_area_fails_at_registration(Type entity, string reason)
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(entity));

        Assert.StartsWith(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_condition_names_every_required_field_without_a_value_and_no_other()
    {
        var grade = Field("Grade", required: true);
        var room = Field("Room", required: true);
        var year = Field("Year", required: true, AttributeValueType.Integer);
        var notes = Field("Notes", required: false);
        var parent = new TestParent { Name = "Parent" };

        Assert.Equal(["Grade, Room and Year are required"], CustomFieldReasons(parent, grade, room, year, notes));

        parent.Attributes.Add(new TestParentAttribute { EntityAttributeDefinitionId = room.Id, StringValue = "North" });
        Assert.Equal(["Grade and Year are required"], CustomFieldReasons(parent, grade, room, year, notes));

        parent.Attributes.Add(new TestParentAttribute { EntityAttributeDefinitionId = grade.Id, StringValue = "   " });
        parent.Attributes.Add(new TestParentAttribute { EntityAttributeDefinitionId = year.Id, Int32Value = 7 });
        Assert.Equal(["Grade is required"], CustomFieldReasons(parent, grade, room, year, notes));

        parent.Attributes.First(value => value.EntityAttributeDefinitionId == grade.Id).StringValue = "7";
        Assert.Empty(CustomFieldReasons(parent, grade, room, year, notes));
    }

    [Fact]
    public void A_field_no_value_can_fill_and_a_field_that_is_not_required_never_block_the_step()
    {
        var retired = Field("Legacy", required: true, (AttributeValueType)5);
        var optional = Field("Room", required: false);

        Assert.Empty(CustomFieldReasons(new TestParent { Name = "Parent" }, retired, optional));
    }

    [Fact]
    public void The_condition_refuses_rather_than_passes_when_it_cannot_see_the_values_or_the_fields()
    {
        var parent = new TestParent { Name = "Parent" };
        var workflow = WorkflowRegistry.Default.Get<TestParentState, TestParent>();

        var noFields = workflow.GetBlockingReasons(parent, "test-parent-activate", new WorkflowEnvironment(TimeProvider.System, NavigationLoadState.Complete));
        Assert.Contains($"{RequiredAttributesCondition<TestParentState, TestParent, TestParentAttribute>.Requirement} (cannot be checked: the custom fields were not read)", noFields);

        var notLoaded = workflow.GetBlockingReasons(parent, "test-parent-activate", new WorkflowEnvironment(TimeProvider.System, NavigationLoadState.None, customFields: TestCustomFields.NoParentFields));
        Assert.Contains($"{RequiredAttributesCondition<TestParentState, TestParent, TestParentAttribute>.Requirement} (cannot be checked: Attributes not loaded)", notLoaded);

        var otherTable = workflow.GetBlockingReasons(parent, "test-parent-activate", new WorkflowEnvironment(TimeProvider.System, NavigationLoadState.Complete, customFields: CustomFieldCatalog.Of(typeof(TestFolderAttributeDefinition), [])));
        Assert.Contains($"{RequiredAttributesCondition<TestParentState, TestParent, TestParentAttribute>.Requirement} (cannot be checked: the custom fields were not read)", otherTable);
    }

    [Fact]
    public void The_condition_takes_only_the_entitys_own_custom_field_collection()
    {
        Assert.Throws<ArgumentException>(() => new RequiredAttributesCondition<TestParentState, TestParent, TestParentAttribute>(parent => parent.Attributes.Where(value => value.StringValue != null).ToList()));
        Assert.Equal(typeof(TestParentAttributeDefinition), new RequiredAttributesCondition<TestParentState, TestParent, TestParentAttribute>(parent => parent.Attributes).DefinitionType);
    }

    [Fact]
    public void A_choice_fills_its_field_with_a_key_the_field_has_retired_or_not_and_never_with_one_it_does_not_have()
    {
        var size = Field("Size", required: true, AttributeValueType.Choice);
        size.Options = [new("small", "Small"), new("large", "Large", IsRetired: true)];
        var parent = new TestParent { Name = "Parent" };

        parent.Attributes.Add(new TestParentAttribute { EntityAttributeDefinitionId = size.Id, StringValue = "huge" });
        Assert.Equal(["Size is required"], CustomFieldReasons(parent, size));

        parent.Attributes.Single().StringValue = "large";
        Assert.Empty(CustomFieldReasons(parent, size));

        parent.Attributes.Single().StringValue = "small";
        Assert.Empty(CustomFieldReasons(parent, size));
    }

    [Fact]
    public void The_steps_that_need_the_required_fields_give_IsRequired_its_two_meanings_by_state()
    {
        var workflow = WorkflowRegistry.Default.Get<TestParentState, TestParent>();
        var table = typeof(TestParentAttributeDefinition);

        Assert.Equal(["test-parent-activate", "test-parent-confirm"], workflow.StepsRequiringCustomFields(table).Select(step => step.Key));
        Assert.Equal(["test-parent-activate"], workflow.StepsRequiringCustomFieldsFrom(TestParentState.Draft, table).Select(step => step.Key));
        Assert.Empty(workflow.StepsRequiringCustomFieldsFrom(TestParentState.Confirmed, table));

        Assert.Equal([TestParentState.Active, TestParentState.Confirmed], Enum.GetValues<TestParentState>().Where(state => workflow.RequiresCustomFieldsIn(state, table)));
        Assert.True(((IWorkflow)workflow).RequiresCustomFieldsIn(TestParentState.Confirmed, table));
        Assert.False(((IWorkflow)workflow).RequiresCustomFieldsIn(TestParentState.Draft, table));

        Assert.Empty(workflow.StepsRequiringCustomFields(typeof(TestFolderAttributeDefinition)));
    }

    [Fact]
    public void A_field_can_be_required_only_for_an_entity_whose_workflow_has_a_step_that_needs_it()
    {
        Assert.True(CustomFieldRequirements.CanBeRequired(typeof(TestParentAttributeDefinition)));

        Assert.False(CustomFieldRequirements.CanBeRequired(typeof(TestFolderAttributeDefinition)));
        Assert.False(CustomFieldRequirements.CanBeRequired(typeof(Staffora.Core.Entities.EntityAttributes.UserAttributes.ApplicationUserAttributeDefinition)));
        Assert.False(CustomFieldRequirements.CanBeRequired(typeof(Staffora.Core.Entities.EntityAttributes.OrganizationAttributes.OrganizationUnitAttributeDefinition)));
        Assert.True(CustomFieldRequirements.CanBeRequired(typeof(Staffora.Core.Entities.EntityAttributes.JobAttributes.JobAttributeDefinition)));
        Assert.Equal([CustomFieldTarget.Job], Enum.GetValues<CustomFieldTarget>().Where(Staffora.Services.CustomFields.CustomFieldTargets.CanBeRequired));
        Assert.False(CustomFieldRequirements.CanBeRequired(typeof(string)));
    }

    private static List<string> CustomFieldReasons(TestParent parent, params TestParentAttributeDefinition[] fields)
    {
        var workflow = WorkflowRegistry.Default.Get<TestParentState, TestParent>();
        var environment = new WorkflowEnvironment(TimeProvider.System, NavigationLoadState.Complete, customFields: CustomFieldCatalog.Of(typeof(TestParentAttributeDefinition), fields));
        return [.. workflow.GetBlockingReasons(parent, "test-parent-activate", environment).Where(reason => reason.EndsWith("required", StringComparison.Ordinal) && reason != "Name is required")];
    }

    private static TestParentAttributeDefinition Field(string name, bool required, AttributeValueType type = AttributeValueType.Text) =>
        new() { Id = Guid.NewGuid(), Name = name, ValueType = type, IsRequired = required };
}
