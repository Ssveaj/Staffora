using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core;
using Staffora.Core.Entities;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Unit.Data;

namespace Staffora.Test.Unit.Testing;

public class TestStateEntityTests
{
    private static readonly IModel testModel = BuildTestModel();

    private static readonly Type[] testEntities = [typeof(TestParent), typeof(TestChild), typeof(TestGrandchild)];

    [Fact]
    public void The_test_workflows_register_with_a_delete_state_each()
    {
        var workflows = testEntities.Select(WorkflowRegistry.Default.Get).ToList();

        Assert.All(workflows, workflow => Assert.Contains(workflow.States, state => state.IsDeleteState()));
    }

    [Fact]
    public async Task Deleting_a_test_parent_cascades_to_its_child_and_grandchild()
    {
        var parent = new TestParent { Name = "Parent" };
        var child = new TestChild { Name = "Child", Parent = parent };
        child.Grandchildren.Add(new TestGrandchild { Name = "Grandchild", Child = child });
        parent.Children.Add(child);
        var environment = new WorkflowEnvironment(TimeProvider.System, NavigationLoadState.Complete);

        var outcome = await WorkflowRegistry.Default.Get<TestParentState, TestParent>()
            .ExecuteTransitionAsync(parent, "test-parent-delete", environment, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["test-parent-delete", "test-child-delete", "test-grandchild-delete"], outcome.Steps.Select(step => step.TransitionKey));
        Assert.Equal(TestGrandchildState.Deleted, child.Grandchildren.Single().State);
    }

    [Fact]
    public void The_test_entities_are_tenant_rows_with_the_tenant_filter()
    {
        var entityTypes = testEntities.Select(type => testModel.FindEntityType(type)!).ToList();

        Assert.All(entityTypes, entityType => Assert.True(typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType)));
        Assert.All(entityTypes, entityType => Assert.NotEmpty(entityType.GetDeclaredQueryFilters()));
        Assert.All(entityTypes, entityType => Assert.Contains(entityType.GetIndexes(), index => index.Properties[0].Name == nameof(ITenantEntity.TenantId)));
    }

    [Fact]
    public void Both_state_base_types_get_State_and_IsInTransition_as_their_only_concurrency_tokens()
    {
        var stateEntities = StateEntityConfigurationTests.StateEntities(testModel);

        Assert.Contains(stateEntities, entityType => entityType.ClrType.BaseType == typeof(StateEntityBase<Core.Entities.Tenancy.TenantState>));
        Assert.Contains(stateEntities, entityType => entityType.ClrType.BaseType == typeof(TenantStateEntityBase<TestParentState>));
        Assert.Empty(StateEntityConfigurationTests.TokenViolations(stateEntities));
    }

    private static IModel BuildTestModel()
    {
        using var context = new TestDataContext(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));
        return context.GetService<IDesignTimeModel>().Model;
    }
}
