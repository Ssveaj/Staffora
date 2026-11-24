using Staffora.Core;
using Staffora.Core.Annotations;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.CustomFields;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class CustomFieldLifecycleTests
{
    public static TheoryData<string, CustomFieldState, CustomFieldState> Edges => new()
    {
        { "field-create", CustomFieldState.Create, CustomFieldState.Draft },
        { "field-save-draft", CustomFieldState.Draft, CustomFieldState.Draft },
        { "field-activate", CustomFieldState.Draft, CustomFieldState.Active },
        { "field-save-active", CustomFieldState.Active, CustomFieldState.Active },
        { "field-deactivate", CustomFieldState.Active, CustomFieldState.Inactive },
        { "field-save-inactive", CustomFieldState.Inactive, CustomFieldState.Inactive },
        { "field-reactivate", CustomFieldState.Inactive, CustomFieldState.Active },
    };

    private static IEnumerable<IWorkflow> Workflows =>
    [
        WorkflowRegistry.Default.Get(typeof(ApplicationUserAttributeDefinition)),
        WorkflowRegistry.Default.Get(typeof(OrganizationUnitAttributeDefinition)),
        WorkflowRegistry.Default.Get(typeof(JobAttributeDefinition)),
    ];

    private static Workflow<CustomFieldState, ApplicationUserAttributeDefinition> UserFields => WorkflowRegistry.Default.Get<CustomFieldState, ApplicationUserAttributeDefinition>();

    private static Workflow<CustomFieldState, JobAttributeDefinition> JobFields => WorkflowRegistry.Default.Get<CustomFieldState, JobAttributeDefinition>();

    [Fact]
    public void The_states_keep_their_stored_integers_and_the_safe_default_is_the_draft()
    {
        Assert.Equal(
            [(CustomFieldState.Create, -1), (CustomFieldState.Draft, 0), (CustomFieldState.Active, 1), (CustomFieldState.Inactive, 2)],
            Enum.GetValues<CustomFieldState>().OrderBy(state => (int)state).Select(state => (state, (int)state)));
        Assert.Equal(CustomFieldState.Draft, new ApplicationUserAttributeDefinition().State);
        Assert.True(((Enum)CustomFieldState.Create).IsEntryState());
        Assert.Equal(StateCategory.Archived, ((Enum)CustomFieldState.Inactive).GetStateCategory());
    }

    [Fact]
    public void Every_definition_table_has_the_lifecycle_with_exactly_its_edges_and_no_delete()
    {
        var expected = Edges.Select(row => (row.Data.Item1, (Enum)row.Data.Item2, (Enum)row.Data.Item3)).ToList();
        foreach (var workflow in Workflows)
        {
            Assert.Equal(expected, workflow.Transitions.Select(transition => (transition.Key, transition.From, transition.To)));
            Assert.All(workflow.Transitions, transition =>
            {
                Assert.True(transition.RequiresPlatformOwner, transition.Key);
                Assert.Null(transition.RequiredPermission);
                Assert.False(transition.IsSystemDriven);
                Assert.False(transition.To.IsDeleteState());
                Assert.False(string.IsNullOrWhiteSpace(transition.Description));
            });
        }

        Assert.DoesNotContain(Enum.GetValues<CustomFieldState>(), state => ((Enum)state).IsDeleteState());
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public async Task Every_edge_executes(string key, CustomFieldState from, CustomFieldState to)
    {
        var field = new ApplicationUserAttributeDefinition { Name = "Badge", ValueType = AttributeValueType.Text, State = from, IsInTransition = true };

        var outcome = await UserFields.ExecuteTransitionAsync(field, key, Environments.NothingLoaded(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(to, field.State);
        Assert.False(field.IsInTransition);
        Assert.Equal((key, (Enum)from, (Enum)to), (Assert.Single(outcome.Steps).TransitionKey, Assert.Single(outcome.Steps).From, Assert.Single(outcome.Steps).To));
    }

    [Fact]
    public void Each_state_stores_its_edits_through_its_one_save()
    {
        Assert.All(UserFields.Transitions, transition => Assert.False(transition.IsSave));
        Assert.Equal("field-save-draft", UserFields.GetEditAuthority(CustomFieldState.Draft)!.Key);
        Assert.Equal("field-save-active", UserFields.GetEditAuthority(CustomFieldState.Active)!.Key);
        Assert.Equal("field-save-inactive", UserFields.GetEditAuthority(CustomFieldState.Inactive)!.Key);
        Assert.True(UserFields.EditsGoWithItsSteps(CustomFieldState.Create));
        Assert.NotNull(UserFields.Transitions.Single(transition => transition.Key == "field-deactivate").ConfirmationMessage);
    }

    [Fact]
    public void A_new_field_chooses_its_tenant_and_type_and_a_saved_one_keeps_them()
    {
        var create = UserFields.GetFieldConfig(CustomFieldState.Create);
        Assert.Equal(FieldAccessFlags.ReadWrite, create.GetFieldAccess(e => e.TenantId));
        Assert.Equal(FieldAccessFlags.Required, create.GetFieldAccess(e => e.ValueType));
        foreach (var state in new[] { CustomFieldState.Draft, CustomFieldState.Active, CustomFieldState.Inactive })
        {
            var config = UserFields.GetFieldConfig(state);
            Assert.Equal(FieldAccessFlags.ReadOnly, config.GetFieldAccess(e => e.TenantId));
            Assert.Equal(FieldAccessFlags.ReadOnly, config.GetFieldAccess(e => e.ValueType));
        }

        foreach (var state in Enum.GetValues<CustomFieldState>())
        {
            var config = UserFields.GetFieldConfig(state);
            Assert.Equal(FieldAccessFlags.Required, config.GetFieldAccess(e => e.Name));
            Assert.Equal(FieldAccessFlags.ReadWrite, config.GetFieldAccess(e => e.Options));
            Assert.Equal(FieldAccessFlags.ReadOnly, config.GetFieldAccess(e => e.RowVersion));
            Assert.Equal(FieldAccessFlags.ReadWrite, JobFields.GetFieldConfig(state).GetFieldAccess(e => e.Audience));
        }
    }

    [Theory]
    [InlineData(CustomFieldTarget.User)]
    [InlineData(CustomFieldTarget.OrganizationUnit)]
    [InlineData(CustomFieldTarget.Job)]
    public void The_required_mark_is_offered_only_where_a_step_needs_the_required_fields(CustomFieldTarget target)
    {
        var workflow = WorkflowRegistry.Default.Get(CustomFieldTargets.DefinitionType(target));
        foreach (var state in Enum.GetValues<CustomFieldState>())
        {
            Assert.Equal(CustomFieldTargets.CanBeRequired(target) ? FieldAccessFlags.ReadWrite : FieldAccessFlags.None, workflow.GetFieldAccess(state, nameof(IEntityAttributeDefinition.IsRequired)));
        }
    }

    [Fact]
    public void A_step_waits_for_a_name_a_choices_options_and_no_required_mark_on_a_yes_or_no()
    {
        var environment = Environments.NothingLoaded();
        var nameless = new ApplicationUserAttributeDefinition { Name = " ", State = CustomFieldState.Create };
        Assert.Contains("Name is required", UserFields.GetBlockingReasons(nameless, "field-create", environment));

        var choice = new ApplicationUserAttributeDefinition { Name = "Team", ValueType = AttributeValueType.Choice, State = CustomFieldState.Create };
        Assert.Equal([CustomFieldWorkflowAttribute<ApplicationUserAttributeDefinition>.ChoiceWithoutOptions], UserFields.GetBlockingReasons(choice, "field-create", environment));
        choice.Options = [new(string.Empty, " ")];
        Assert.Equal([CustomFieldWorkflowAttribute<ApplicationUserAttributeDefinition>.ChoiceWithoutOptions], UserFields.GetBlockingReasons(choice, "field-create", environment));
        choice.Options = [new(string.Empty, "Teaching")];
        Assert.Empty(UserFields.GetBlockingReasons(choice, "field-create", environment));

        var flagged = new JobAttributeDefinition { Name = "Keys", ValueType = AttributeValueType.Boolean, IsRequired = true, State = CustomFieldState.Active };
        Assert.Equal([CustomFieldWorkflowAttribute<JobAttributeDefinition>.YesOrNoNotRequired], JobFields.GetBlockingReasons(flagged, "field-save-active", environment));
    }
}
