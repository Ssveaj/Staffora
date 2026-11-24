using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Unit.Architecture.DialogScanSamples;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

public partial class StateEntityDialogTests
{
    [Fact]
    public async Task In_Draft_a_required_custom_field_is_needed_before_the_activation_and_not_marked_required()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var size = await AddFieldAsync(scenario.A, "Size", AttributeValueType.Choice, required: true, cancellationToken, "Small", "Large");
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        foreach (var (field, focusable) in new[] { (grade, $"[name='test-parent-attribute-{grade}']"), (size, "[role=combobox]") })
        {
            var frame = CustomField(dialog, field);
            Assert.Equal("editable", frame.GetAttribute("data-access"));
            Assert.Null(frame.QuerySelector(".attribute-value-field__required-mark"));
            var input = frame.QuerySelector(focusable)!;
            Assert.False(input.HasAttribute("aria-required"), focusable);
            var hint = frame.QuerySelector(".attribute-value-field__needed-before")!;
            Assert.Equal("Needed before Active", hint.TextContent.Trim());
            Assert.Equal(hint.Id, input.GetAttribute("aria-describedby"));
        }

        Assert.Null(CustomField(dialog, room).QuerySelector(".attribute-value-field__needed-before"));
        Assert.False(Input(dialog, $"test-parent-attribute-{room}").HasAttribute("aria-describedby"));

        Assert.Equal("required", Field(dialog, "Name").GetAttribute("data-access"));
        Assert.Equal("true", Input(dialog, "test-parent-name").GetAttribute("aria-required"));
    }

    [Fact]
    public async Task In_Active_a_locked_required_custom_field_says_nothing_of_what_it_is_needed_for()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken, (grade, "7"));
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Equal("read-only", CustomField(dialog, grade).GetAttribute("data-access"));
        Assert.Null(CustomField(dialog, grade).QuerySelector(".attribute-value-field__needed-before"));
        Assert.False(Input(dialog, $"test-parent-attribute-{grade}").HasAttribute("aria-required"));
    }

    [Fact]
    public async Task In_Confirmed_a_required_custom_field_is_required_with_its_mark_and_aria_required()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Confirmed, "Confirmed parent", cancellationToken, (grade, "7"));
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        var frame = CustomField(dialog, grade);
        Assert.Equal("required", frame.GetAttribute("data-access"));
        Assert.Equal("true", Input(dialog, $"test-parent-attribute-{grade}").GetAttribute("aria-required"));
        Assert.Equal("true", frame.QuerySelector(".attribute-value-field__required-mark")!.GetAttribute("aria-hidden"));
        Assert.Null(frame.QuerySelector(".attribute-value-field__needed-before"));
        Assert.Equal("editable", CustomField(dialog, room).GetAttribute("data-access"));

        Input(dialog, $"test-parent-attribute-{grade}").Change(string.Empty);
        dialog.WaitForEdit();
        dialog.Step("test-parent-save-confirmed").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("Grade is required while the TestParent is Confirmed.", dialog.Alerts(), StringComparison.Ordinal), Wait);
        Assert.Equal("7", (await ValuesOfAsync(id, cancellationToken))[grade]);
    }

    [Fact]
    public async Task In_Draft_a_save_may_empty_a_required_custom_field()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken, (grade, "7"));
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = host.Get<TestParentDialogService>();

        var updater = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        updater.Entity.Attributes.Clear();
        var saved = await service.UpdateTestParentAsync(updater, cancellationToken);

        Assert.True(saved.IsSuccess, saved.Error);
        Assert.Empty(await ValuesOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task In_Confirmed_a_save_may_not_empty_a_required_custom_field_on_any_path_while_it_may_fill_one()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Confirmed, "Confirmed parent", cancellationToken, (grade, "7"), (room, "North"));
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = host.Get<TestParentDialogService>();

        var cleared = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        cleared.Entity.Attributes.Remove(cleared.Entity.Attributes.Single(value => value.EntityAttributeDefinitionId == grade));
        var refused = await service.UpdateTestParentAsync(cleared, cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Equal("Grade is required while the TestParent is Confirmed.", refused.Error);

        var withStep = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        withStep.Entity.Notes = "Sent with the emptied grade";
        withStep.Entity.Attributes.Remove(withStep.Entity.Attributes.Single(value => value.EntityAttributeDefinitionId == grade));
        var refusedStep = await service.UpdateTestParentAndTransitionAsync(withStep, "test-parent-save-confirmed", null, cancellationToken);
        Assert.False(refusedStep.IsSuccess);
        Assert.Contains("Grade is required while the TestParent is Confirmed.", refusedStep.Error, StringComparison.Ordinal);
        Assert.Null((await LoadAsync(id, cancellationToken))!.Notes);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.TestParentAttributes.Remove(await context.TestParentAttributes.SingleAsync(value => value.EntityId == id && value.EntityAttributeDefinitionId == grade, cancellationToken));
            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Contains("Grade must have a value while it is Confirmed", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(["Grade"], refusal.EmptiedCustomFields);
        }

        Assert.Equal(new Dictionary<Guid, string?> { [grade] = "7", [room] = "North" }, await ValuesOfAsync(id, cancellationToken));

        var other = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        other.Entity.Attributes.Remove(other.Entity.Attributes.Single(value => value.EntityAttributeDefinitionId == room));
        other.Entity.Attributes.Single(value => value.EntityAttributeDefinitionId == grade).StringValue = "8";
        var passed = await service.UpdateTestParentAsync(other, cancellationToken);
        Assert.True(passed.IsSuccess, passed.Error);
        Assert.Equal(new Dictionary<Guid, string?> { [grade] = "8" }, await ValuesOfAsync(id, cancellationToken));

        var missing = await AddParentWithValuesAsync(scenario.A, TestParentState.Confirmed, "Missing its grade", cancellationToken);
        var notes = (await service.LoadTestParentUpdaterAsync(missing, cancellationToken)).Value!;
        notes.Entity.Notes = "Still saved";
        Assert.True((await service.UpdateTestParentAsync(notes, cancellationToken)).IsSuccess);
        var repair = (await service.LoadTestParentUpdaterAsync(missing, cancellationToken)).Value!;
        repair.Entity.Attributes.Add(new TestParentAttribute { EntityAttributeDefinitionId = grade, StringValue = "9" });
        var repaired = await service.UpdateTestParentAsync(repair, cancellationToken);
        Assert.True(repaired.IsSuccess, repaired.Error);
        Assert.Equal("9", (await ValuesOfAsync(missing, cancellationToken))[grade]);
    }

    [Fact]
    public async Task A_choice_whose_option_was_retired_still_fills_a_required_field_and_an_unknown_key_is_refused_on_save()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var size = await AddFieldAsync(scenario.A, "Size", AttributeValueType.Choice, required: true, cancellationToken, "Small", "Large");
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken, (size, "small"));

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await context.TestParentAttributes.SingleAsync(value => value.EntityId == id, cancellationToken)).StringValue = "huge";
            var refusal = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Contains("Size has no such option.", refusal.Message, StringComparison.Ordinal);
        }

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        using (writer.BeginSystemScope("test: retire an option as the platform does"))
        {
            var definition = await writer.TestParentAttributeDefinitions.SingleAsync(field => field.Id == size, cancellationToken);
            definition.Options = [.. definition.Options.Select(option => option.Key == "small" ? option with { IsRetired = true } : option)];
            await writer.SaveChangesAsync(cancellationToken);
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var activated = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);
    }

    [Fact]
    public async Task A_global_required_custom_field_holds_the_step_back_on_the_server_and_in_the_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var name = "Global" + Guid.NewGuid().ToString("N")[..6];
        var global = await AddGlobalFieldAsync(name, cancellationToken);
        try
        {
            var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
            await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
            {
                var refused = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);
                Assert.False(refused.IsSuccess);
                Assert.Contains($"{name} is required", refused.Error, StringComparison.Ordinal);
            }

            await using var circuit = Circuit(scenario.A.Runtime());
            var dialog = await circuit.OpenLoadedAsync(id);
            Assert.Equal("true", dialog.Step("test-parent-activate").GetAttribute("aria-disabled"));
            Assert.Contains($"{name} is required", Card(dialog, dialog.Step("test-parent-activate")), StringComparison.Ordinal);
            Assert.Equal("Needed before Active", CustomField(dialog, global).QuerySelector(".attribute-value-field__needed-before")!.TextContent.Trim());
        }
        finally
        {
            await RemoveGlobalFieldAsync(global, cancellationToken);
        }
    }

    [Fact]
    public async Task A_platform_owner_reading_the_steps_of_or_stepping_Tenant_As_parent_is_held_by_Tenant_As_required_fields_alone()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        await AddFieldAsync(scenario.B, "TheirSecretField", AttributeValueType.Text, required: true, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var workflows = host.Get<WorkflowService>();

        var available = await workflows.GetAvailableTransitionsAsync<TestParentState, TestParent>(id, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken);
        Assert.True(available.IsSuccess, available.Error);
        var reasons = string.Join(" | ", available.Value!.SelectMany(answer => answer.BlockingReasons));
        Assert.Contains("Grade is required", reasons, StringComparison.Ordinal);
        Assert.DoesNotContain("TheirSecretField", reasons, StringComparison.Ordinal);
        var activate = Assert.Single(available.Value!, answer => answer.Transition.Key == "test-parent-activate");
        Assert.Equal(["Grade is required"], activate.BlockingReasons.Where(reason => reason.EndsWith("required", StringComparison.Ordinal) && reason != "Name is required"));

        var refused = await workflows.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Grade is required", refused.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("TheirSecretField", refused.Error, StringComparison.Ordinal);
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            writer.TestParentAttributes.Add(new TestParentAttribute { EntityId = id, EntityAttributeDefinitionId = grade, StringValue = "7" });
            await writer.SaveChangesAsync(cancellationToken);
        }

        var activated = await workflows.TransitionEntityAsync<TestParentState, TestParent>(id, "test-parent-activate", cancellationToken: cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);
    }

    [Fact]
    public async Task A_field_made_required_while_the_dialog_was_open_is_refused_by_the_server_with_its_name()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        Assert.Null(dialog.Step("test-parent-activate").GetAttribute("aria-disabled"));
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        using (writer.BeginSystemScope("test: make the field required as the platform does"))
        {
            (await writer.TestParentAttributeDefinitions.SingleAsync(field => field.Id == grade, cancellationToken)).IsRequired = true;
            await writer.SaveChangesAsync(cancellationToken);
        }

        Input(dialog, "test-parent-notes").Change("edited");
        dialog.WaitForEdit();
        dialog.Host.WaitForAssertion(() => Assert.Null(dialog.Step("test-parent-activate").GetAttribute("aria-disabled")), Wait);
        dialog.Step("test-parent-activate").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("Grade is required", dialog.Alerts(), StringComparison.Ordinal), Wait);
        Assert.Equal(TestParentState.Draft, (await LoadAsync(id, cancellationToken))!.State);
        Assert.Null((await LoadAsync(id, cancellationToken))!.Notes);
    }

    [Fact]
    public async Task A_context_for_an_entity_with_custom_fields_that_does_not_load_them_does_not_open()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());

        var context = new FieldlessParentSampleContext(host.Get<TestParentDialogService>(), id);
        context.Attach(host.Get<WorkflowService>(), TimeProvider.System, null, NullLogger.Instance);
        await context.EnsureLoadedAsync();

        Assert.Equal(StateEntityDialogLoadState.CouldNotOpen, context.LoadState);
        Assert.Equal("The TestParent dialog does not load its custom fields.", context.CouldNotOpenReason);
    }

    [Fact]
    public async Task A_reload_after_a_step_asks_for_neither_the_custom_fields_nor_the_editor_options_again()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        var child = await AddChildAsync(scenario.A, id, "Child", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);
        Assert.Equal(1, circuit.Service.CustomFieldReads);
        dialog.Step("test-parent-save").Click();
        dialog.WaitUntilAsked(2);
        Assert.Equal(1, circuit.Service.CustomFieldReads);
        await dialog.CloseByEscAsync();

        var childDialog = await circuit.OpenChildLoadedAsync(child);
        Assert.Equal(1, circuit.ChildService.EditorOptionReads);
        childDialog.Step("test-child-activate").Click();
        childDialog.WaitUntilAsked(2);
        Assert.Equal(1, circuit.ChildService.EditorOptionReads);
    }

    [Fact]
    public async Task A_body_that_changes_the_entitys_state_in_memory_changes_no_fields_access()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var draft = await AddParentAsync(scenario.A, TestParentState.Draft, "Draft parent", cancellationToken);
        var active = await AddParentAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var first = await circuit.OpenLoadedAsync(draft);
        await first.Host.InvokeAsync(() => { first.Context().Entity!.State = TestParentState.Canceled; first.Context().NotifyChanged(); });
        Assert.Equal("editable", Field(first, "Notes").GetAttribute("data-access"));
        Assert.Equal("required", Field(first, "Name").GetAttribute("data-access"));
        await first.Host.InvokeAsync(() => first.Context().Entity!.State = TestParentState.Draft);
        await first.CloseByEscAsync();

        var second = await circuit.OpenLoadedAsync(active);
        await second.Host.InvokeAsync(() => { second.Context().Entity!.State = TestParentState.Draft; second.Context().NotifyChanged(); });
        Assert.Equal("read-only", Field(second, "Name").GetAttribute("data-access"));
        Assert.True(Input(second, "test-parent-name").HasAttribute("readonly"));
    }

    [Fact]
    public async Task A_read_only_field_takes_no_edit_whatever_event_reaches_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken, (grade, "7"));
        var other = await AddParentAsync(scenario.A, TestParentState.Draft, "Other parent", cancellationToken);
        var child = await AddChildAsync(scenario.A, other, "Child", cancellationToken);
        await MoveChildAsync(scenario, child, "test-child-activate", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);

        Input(dialog, "test-parent-name").Change("Changed");
        await dialog.Host.InvokeAsync(() => TextBox(dialog, "test-parent-name").ValueChanged.InvokeAsync("Changed"));
        await dialog.Host.InvokeAsync(() => TextBox(dialog, $"test-parent-attribute-{grade}").ValueChanged.InvokeAsync("8"));

        Assert.Equal("Active parent", dialog.Context().Entity!.Name);
        Assert.Equal("7", dialog.Context().Entity!.Attributes.Single().StringValue);
        Assert.False(dialog.Context().HasChanges);
        await dialog.CloseByEscAsync();

        var childDialog = await circuit.OpenChildLoadedAsync(child);
        Assert.DoesNotContain(childDialog.Host.FindComponents<Radzen.Blazor.RadzenDropDown<Guid>>(), dropDown => dropDown.Instance.Name == "test-child-parent-id");
        Assert.True(childDialog.Host.Find("input[name='test-child-parent-id']").HasAttribute("readonly"));
        var context = childDialog.AnyContext<TestChildState, TestChild>();
        Assert.Equal(other, context.Entity!.ParentId);
        Assert.Equal(other, context.Entity.Parent!.Id);
        Assert.False(context.HasChanges);
    }

    private static Radzen.Blazor.RadzenTextBox TextBox(OpenDialog dialog, string name) =>
        dialog.Host.FindComponents<Radzen.Blazor.RadzenTextBox>().Last(textBox => textBox.Instance.Name == name).Instance;

    private async Task<Guid> AddGlobalFieldAsync(string name, CancellationToken cancellationToken)
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var writer = this.database.CreateContext(platform.PlatformOwnerRuntime());
        using var lease = writer.BeginSystemScope("test: a global custom field of the parents");
        var field = new TestParentAttributeDefinition { State = CustomFieldState.Active, Name = name, ValueType = AttributeValueType.Text, TenantId = null, IsRequired = true };
        writer.TestParentAttributeDefinitions.Add(field);
        await writer.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task RemoveGlobalFieldAsync(Guid field, CancellationToken cancellationToken)
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var deactivated = await console.Get<WorkflowService>().TransitionEntityAsync<CustomFieldState, TestParentAttributeDefinition>(field, CustomFieldWorkflowAttribute<TestParentAttributeDefinition>.DeactivateStep, cancellationToken: cancellationToken);
        Assert.True(deactivated.IsSuccess, deactivated.Error);
    }
}
