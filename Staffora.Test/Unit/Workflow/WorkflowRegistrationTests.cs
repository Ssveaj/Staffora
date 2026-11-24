using Staffora.Core.Workflow;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class WorkflowRegistrationTests
{
    [Fact]
    public void A_cascade_naming_a_child_state_without_that_edge_fails_at_registration()
    {
        var registry = new WorkflowRegistry();

        var error = Assert.Throws<WorkflowDefinitionException>(() => registry.Get(typeof(CascadeWithoutEdgeEntity)));

        Assert.Equal("The cascade at CascadeWithoutEdgeEntity.Closed moves SampleChild to Imported, but the SampleChild workflow has no transition into Imported.", error.Message);
    }

    [Fact]
    public void A_cascade_to_a_child_state_with_an_edge_registers()
    {
        var workflow = new WorkflowRegistry().Get(typeof(CascadeWithEdgeEntity));

        Assert.Equal(typeof(CascadeWithEdgeEntity), workflow.EntityType);
    }

    [Fact]
    public void A_cascade_into_a_delete_state_that_leaves_a_child_state_alone_fails_at_registration()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(DeleteCascadeLeavingAloneEntity)));

        Assert.Equal(
            "The cascade at DeleteCascadeLeavingAloneEntity.Closed moves SampleChild to the delete state Deleted, so it cannot leave SampleChild alone in Declined: a child left behind would still refer to the removed parent. Block the delete with a condition instead.",
            error.Message);
    }

    [Fact]
    public void A_failed_registration_fails_every_later_lookup_too()
    {
        var registry = new WorkflowRegistry();

        Assert.Throws<WorkflowDefinitionException>(() => registry.Get(typeof(CascadeWithoutEdgeEntity)));
        Assert.Throws<WorkflowDefinitionException>(() => registry.Get(typeof(CascadeWithoutEdgeEntity)));
    }

    [Fact]
    public void A_workflow_missing_a_state_fails_at_registration()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(MissingStateEntity)));

        Assert.Contains("does not register Closed", error.Message);
    }

    [Fact]
    public void A_condition_reading_an_unknown_navigation_fails_at_registration()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(UnknownReadEntity)));

        Assert.Contains("reads Times", error.Message);
    }

    [Fact]
    public void A_transition_asking_both_a_confirmation_and_a_comment_fails_at_registration()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(ConfirmedAndPromptedEntity)));

        Assert.Equal("ConfirmedAndPromptedEntity transition 'broken-close' asks both a confirmation and a comment; a step asks one or the other.", error.Message);
    }

    [Fact]
    public void A_comment_prompt_on_an_entity_without_comment_threads_fails_at_registration()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(UncommentedPromptEntity)));

        Assert.Equal("UncommentedPromptEntity transition 'broken-close' asks a comment, but UncommentedPromptEntity has no comment threads (ICommentedEntity), so the answer would be lost.", error.Message);
    }

    [Fact]
    public void A_comment_prompt_on_a_step_into_a_delete_state_fails_at_registration()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(PromptedDeleteEntity)));

        Assert.Equal("PromptedDeleteEntity transition 'broken-delete' asks a comment on a step into a delete state; the comment would outlive the record it explains.", error.Message);
    }

    [Theory]
    [InlineData(typeof(TwoSavesEntity), "TwoSavesEntity.Open marks 'broken-save' and 'broken-save-again' IsSave; a state has at most one save step.")]
    [InlineData(typeof(LeavingSaveEntity), "LeavingSaveEntity transition 'broken-save' is marked IsSave, but it leads from Open to Closed, and a save stays in its state; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(ConfirmedSaveEntity), "ConfirmedSaveEntity transition 'broken-save' is marked IsSave, but it asks a confirmation; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(PromptedSaveEntity), "PromptedSaveEntity transition 'broken-save' is marked IsSave, but it asks a comment; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(ActingSaveEntity), "ActingSaveEntity transition 'broken-save' is marked IsSave, but it runs actions; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(EnteringSaveEntity), "EnteringSaveEntity transition 'broken-save' is marked IsSave, but it runs actions; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(ExitingSaveEntity), "ExitingSaveEntity transition 'broken-save' is marked IsSave, but it runs actions; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(SystemSaveEntity), "SystemSaveEntity transition 'broken-save' is marked IsSave, but it is system-driven; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(OwnerSaveEntity), "OwnerSaveEntity transition 'broken-save' is marked IsSave, but only a platform owner may take it; the save step a dialog runs on its own stores the edits and does nothing else.")]
    [InlineData(typeof(NoEditsSaveEntity), "NoEditsSaveEntity transition 'broken-save' is marked IsSave, but it takes no edits; the save step a dialog runs on its own stores the edits and does nothing else.")]
    public void A_save_step_designated_wrongly_fails_at_registration(Type entity, string message)
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(entity));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Each_state_names_its_save_step_and_a_state_without_one_names_none()
    {
        var workflow = WorkflowRegistry.Default.Get<Staffora.Test.Integration.Infrastructure.TestParentState, Staffora.Test.Integration.Infrastructure.TestParent>();

        Assert.Equal("test-parent-save", workflow.GetSaveStep(Staffora.Test.Integration.Infrastructure.TestParentState.Draft)?.Key);
        Assert.False(workflow.GetTransition("test-parent-file").IsSave);
        Assert.Equal("test-parent-save-confirmed", ((IWorkflow)workflow).GetSaveStep(Staffora.Test.Integration.Infrastructure.TestParentState.Confirmed)?.Key);
        Assert.Null(workflow.GetSaveStep(Staffora.Test.Integration.Infrastructure.TestParentState.Active));

        Assert.All(Enum.GetValues<Staffora.Core.Entities.Tenancy.TenantState>(), state => Assert.Null(WorkflowRegistry.Default.Get<Staffora.Core.Entities.Tenancy.TenantState, Staffora.Core.Entities.Tenancy.Tenant>().GetSaveStep(state)));
    }

    [Fact]
    public void A_condition_reading_a_column_a_primitive_collection_or_a_complex_type_fails_at_registration()
    {
        var error = Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(ColumnReadEntity)));

        Assert.Equal(
            "A condition on ColumnReadEntity transition 'broken-close' reads Name, Tags, Address, which ColumnReadEntity does not have as a navigation (a reference or a collection of entities).",
            error.Message);
    }

    [Fact]
    public void A_type_without_a_workflow_attribute_is_refused()
    {
        Assert.False(WorkflowRegistry.HasWorkflow(typeof(FakeClock)));
        Assert.Throws<WorkflowDefinitionException>(() => new WorkflowRegistry().Get(typeof(FakeClock)));
    }

    [Fact]
    public void A_duplicate_transition_key_is_refused()
    {
        var workflow = new Workflow<BrokenState, CountedEntity>();
        workflow.AddTransition(new("close", BrokenState.Open, BrokenState.Closed));

        Assert.Throws<WorkflowDefinitionException>(() => workflow.AddTransition(new("close", BrokenState.Closed, BrokenState.Open)));
    }

    [Fact]
    public void A_registered_workflow_can_no_longer_change()
    {
        var workflow = Environments.Parent;

        Assert.True(workflow.IsRegistered);
        Assert.Throws<InvalidOperationException>(() => workflow.AddState(ParentState.Draft));
        Assert.Throws<InvalidOperationException>(() => workflow.GetTransition("parent-save").AddCondition(
            new Core.Workflow.Conditions.DelegateCondition<ParentState, SampleParent>("Never", _ => false)));
        Assert.Throws<InvalidOperationException>(() => workflow[ParentState.Draft].ConfigureFields(f => f.Default(Core.Workflow.FieldAccess.FieldAccessFlags.None)));
    }

    [Fact]
    public void An_unregistered_workflow_does_not_run()
    {
        var workflow = (Workflow<BrokenState, CountedEntity>)new CountedWorkflowAttribute().CreateWorkflow();

        Assert.Throws<InvalidOperationException>(() => workflow.CanTransition(new CountedEntity(), "counted-close", Environments.Complete()));
    }

    [Fact]
    public async Task Concurrent_lookups_build_and_register_one_workflow()
    {
        var registry = new WorkflowRegistry();
        using var probe = CountedWorkflowAttribute.StartProbe();
        const int lookups = 8;
        using var started = new CountdownEvent(lookups);

        probe.Gate.Reset();
        var tasks = Enumerable.Range(0, lookups)
            .Select(_ => Task.Run(() =>
            {
                started.Signal();
                return registry.Get(typeof(CountedEntity));
            }, TestContext.Current.CancellationToken))
            .ToList();

        started.Wait(TestContext.Current.CancellationToken);
        probe.Gate.Set();
        var workflows = await Task.WhenAll(tasks);

        Assert.Equal(1, probe.Builds);
        Assert.All(workflows, workflow => Assert.Same(workflows[0], workflow));
    }

    [Fact]
    public void GetWorkflow_returns_the_registered_workflow_of_the_entity_type()
    {
        var parent = new SampleParent();

        Assert.Same(WorkflowRegistry.Default.Get(typeof(SampleParent)), parent.GetWorkflow<ParentState, SampleParent>());
    }
}
