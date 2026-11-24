using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Test.Integration.Infrastructure;

[TestParentFieldWorkflow]
public class TestParentAttributeDefinition : EntityAttributeDefinitionBase<TestParent>;

public sealed class TestParentFieldWorkflowAttribute : CustomFieldWorkflowAttribute<TestParentAttributeDefinition>
{
    protected override FieldAccessFlags RequiredMarkAccess => FieldAccessFlags.ReadWrite;
}

public class TestParentAttribute : EntityAttributeBase<TestParent, TestParentAttributeDefinition>;

[TestFolderFieldWorkflow]
public class TestFolderAttributeDefinition : EntityAttributeDefinitionBase<TestFolder>;

public sealed class TestFolderFieldWorkflowAttribute : CustomFieldWorkflowAttribute<TestFolderAttributeDefinition>;

public class TestFolderAttribute : EntityAttributeBase<TestFolder, TestFolderAttributeDefinition>;

public static class TestCustomFields
{
    public static ICustomFieldCatalog NoParentFields { get; } = CustomFieldCatalog.Of(typeof(TestParentAttributeDefinition), []);
}
