using AngleSharp.Dom;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

public partial class StateEntityDialogTests
{
    [Fact]
    public async Task In_Draft_the_fields_are_editable_the_name_is_marked_required_and_a_required_custom_field_is_needed_before_the_activation()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        var name = Field(dialog, "Name");
        Assert.Equal("required", name.GetAttribute("data-access"));
        Assert.Equal("true", Input(dialog, "test-parent-name").GetAttribute("aria-required"));
        Assert.Equal("true", name.QuerySelector(".state-field__required-mark")!.GetAttribute("aria-hidden"));
        Assert.Equal("Name", name.QuerySelector("label[for=test-parent-name] .state-field__label")!.TextContent);

        foreach (var (field, input) in new[] { ("Notes", "test-parent-notes"), ("Priority", "test-parent-priority"), ("StartsOn", "test-parent-starts-on") })
        {
            Assert.Equal("editable", Field(dialog, field).GetAttribute("data-access"));
            Assert.False(Input(dialog, input).HasAttribute("readonly"), input);
            Assert.False(Input(dialog, input).HasAttribute("aria-required"), input);
            Assert.NotNull(dialog.Host.Find($".state-field[data-field={field}] label[for={input}]"));
        }

        var planning = dialog.Host.Find("fieldset.state-area-fieldset[data-area=Planning]");
        Assert.Equal("editable", planning.GetAttribute("data-access"));
        Assert.Equal("Planning", planning.QuerySelector("legend")!.TextContent.Trim());

        Assert.Equal("editable", CustomField(dialog, grade).GetAttribute("data-access"));
        Assert.False(Input(dialog, $"test-parent-attribute-{grade}").HasAttribute("aria-required"));
        Assert.Equal("Needed before Active", CustomField(dialog, grade).QuerySelector(".attribute-value-field__needed-before")!.TextContent.Trim());
        Assert.Equal("editable", CustomField(dialog, room).GetAttribute("data-access"));
        Assert.False(Input(dialog, $"test-parent-attribute-{room}").HasAttribute("aria-required"));
    }

    [Fact]
    public async Task In_Active_the_locked_fields_are_read_only_and_focusable_while_the_notes_stay_editable()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Choice, required: false, cancellationToken, "North", "South");
        var flag = await AddFieldAsync(scenario.A, "Flagged", AttributeValueType.Boolean, required: false, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken, (grade, "7"), (room, "north"));
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Empty(CustomField(dialog, room).QuerySelectorAll("[role=combobox]"));
        var choice = CustomField(dialog, room).QuerySelector("input")!;
        Assert.True(choice.HasAttribute("readonly"));
        Assert.NotEqual("-1", choice.GetAttribute("tabindex"));
        Assert.Equal("North", choice.GetAttribute("value"));
        Assert.Empty(CustomField(dialog, flag).QuerySelectorAll("[role=checkbox]"));
        Assert.True(Input(dialog, $"test-parent-attribute-{flag}").HasAttribute("readonly"));
        Assert.Equal("No", Input(dialog, $"test-parent-attribute-{flag}").GetAttribute("value"));
        Assert.NotNull(dialog.Host.Find($"label[for=test-parent-attribute-{flag}]"));

        foreach (var (field, input) in new[] { ("Name", "test-parent-name"), ("Priority", "test-parent-priority"), ("StartsOn", "test-parent-starts-on") })
        {
            Assert.Equal("read-only", Field(dialog, field).GetAttribute("data-access"));
            Assert.True(Input(dialog, input).HasAttribute("readonly"), input);
            Assert.False(Input(dialog, input).HasAttribute("disabled"), input);
            Assert.NotEqual("-1", Input(dialog, input).GetAttribute("tabindex"));
            Assert.False(Input(dialog, input).HasAttribute("aria-required"), input);
        }

        Assert.Equal("Active parent", Input(dialog, "test-parent-name").GetAttribute("value"));
        Assert.Equal("editable", Field(dialog, "Notes").GetAttribute("data-access"));
        Assert.False(Input(dialog, "test-parent-notes").HasAttribute("readonly"));

        Assert.Equal("read-only", dialog.Host.Find("fieldset.state-area-fieldset[data-area=Planning]").GetAttribute("data-access"));
        Assert.Contains("(read-only)", dialog.Host.Find("fieldset.state-area-fieldset[data-area=Planning] legend").TextContent, StringComparison.Ordinal);
        Assert.Equal("read-only", CustomField(dialog, grade).GetAttribute("data-access"));
        Assert.True(Input(dialog, $"test-parent-attribute-{grade}").HasAttribute("readonly"));
        Assert.Equal("7", Input(dialog, $"test-parent-attribute-{grade}").GetAttribute("value"));
        Assert.Null(CustomField(dialog, grade).QuerySelector(".attribute-value-field__required-mark"));
    }

    [Fact]
    public async Task In_Canceled_a_hidden_field_a_hidden_area_and_the_hidden_custom_fields_render_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Canceled, "Canceled parent", cancellationToken, (grade, "7"));
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        Assert.Equal("read-only", Field(dialog, "Name").GetAttribute("data-access"));
        Assert.Empty(dialog.Host.FindAll("[name=test-parent-notes]"));
        Assert.Empty(dialog.Host.FindAll(".state-field[data-field=Notes]"));
        Assert.Empty(dialog.Host.FindAll("fieldset.state-area-fieldset[data-area=Planning]"));
        Assert.Empty(dialog.Host.FindAll("[name=test-parent-priority]"));
        Assert.Empty(dialog.Host.FindAll(".custom-fields-fieldset"));
        Assert.Empty(dialog.Host.FindAll($"[data-attribute-field='{grade}']"));
    }

    [Fact]
    public async Task The_childs_drop_down_and_switch_are_editable_in_Draft_and_read_only_once_it_is_active()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var parent = await AddParentAsync(scenario.A, TestParentState.Draft, "First parent", cancellationToken);
        var child = await AddChildAsync(scenario.A, parent, "Child", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var draft = await circuit.OpenChildLoadedAsync(child);
        Assert.Equal("editable", Field(draft, "ParentId").GetAttribute("data-access"));
        Assert.Equal("Parent", Input(draft, "test-child-parent-id").GetAttribute("aria-label"));
        Assert.Equal("editable", draft.Host.Find(".state-field-switch[data-field=IsPinned]").GetAttribute("data-access"));
        Assert.NotNull(draft.Host.Find(".state-field-switch label[for=test-child-is-pinned]"));
        Assert.Equal("switch", Input(draft, "test-child-is-pinned").GetAttribute("role"));
        Assert.False(Input(draft, "test-child-is-pinned").HasAttribute("inert"));
        await draft.CloseByEscAsync();

        await MoveChildAsync(scenario, child, "test-child-activate", cancellationToken);
        var active = await circuit.OpenChildLoadedAsync(child);

        Assert.Equal("read-only", Field(active, "ParentId").GetAttribute("data-access"));
        Assert.Empty(Field(active, "ParentId").QuerySelectorAll("[role=combobox]"));
        var chosen = Field(active, "ParentId").QuerySelector("input")!;
        Assert.True(chosen.HasAttribute("readonly"));
        Assert.NotEqual("-1", chosen.GetAttribute("tabindex"));
        Assert.NotEqual("true", chosen.GetAttribute("aria-disabled"));
        Assert.Equal("read-only", Field(active, "Name").GetAttribute("data-access"));

        Assert.Equal("read-only", Field(active, "IsPinned").GetAttribute("data-access"));
        Assert.Empty(active.Host.FindAll(".state-field-switch"));
        Assert.True(Input(active, "test-child-is-pinned").HasAttribute("readonly"));
        Assert.Equal("No", Input(active, "test-child-is-pinned").GetAttribute("value"));
        Assert.False(Input(active, "test-child-is-pinned").HasAttribute("disabled"));
        Assert.NotEqual("-1", Input(active, "test-child-is-pinned").GetAttribute("tabindex"));
        Assert.NotNull(active.Host.Find("label[for=test-child-is-pinned]"));
    }

    [Fact]
    public async Task An_edit_in_a_state_field_and_in_a_custom_field_reaches_the_header_through_the_edit_channel()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);
        Input(dialog, "test-parent-priority").Change("5");
        dialog.WaitForEdit();
        Assert.Equal(5, dialog.Context().Entity!.Priority);

        var second = await circuit.OpenLoadedAsync(id);
        Input(second, $"test-parent-attribute-{room}").Change("North");
        second.WaitForEdit();
        Assert.Equal(["North"], second.Context().Entity!.Attributes.Select(value => value.StringValue));
        Input(second, $"test-parent-attribute-{room}").Change(" ");
        second.Host.WaitForAssertion(() => Assert.Null(second.Host.FindAll(".state-entity-dialog").Last().QuerySelector(".state-entity-dialog-header__unsaved")), Wait);
        Assert.Empty(second.Context().Entity!.Attributes);
        Assert.False(second.Context().HasChanges);
    }

    [Fact]
    public async Task A_required_custom_field_greys_the_activation_with_its_reason_until_it_is_filled_in_memory()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Parent", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenLoadedAsync(id);

        var step = dialog.Step("test-parent-activate");
        Assert.Equal("true", step.GetAttribute("aria-disabled"));
        Assert.Contains("Grade is required", Card(dialog, step), StringComparison.Ordinal);

        var asked = dialog.Context().OfferRequests;
        Input(dialog, $"test-parent-attribute-{grade}").Change("7");
        dialog.WaitForEdit();
        dialog.Host.WaitForAssertion(() => Assert.Null(dialog.Step("test-parent-activate").GetAttribute("aria-disabled")), Wait);

        Input(dialog, $"test-parent-attribute-{grade}").Change(string.Empty);
        dialog.Host.WaitForAssertion(() => Assert.Equal("true", dialog.Step("test-parent-activate").GetAttribute("aria-disabled")), Wait);
        Assert.Contains("Grade is required", Card(dialog, dialog.Step("test-parent-activate")), StringComparison.Ordinal);
        Assert.Equal(asked, dialog.Context().OfferRequests);

        Input(dialog, $"test-parent-attribute-{grade}").Change("8");
        dialog.Host.WaitForAssertion(() => Assert.Null(dialog.Step("test-parent-activate").GetAttribute("aria-disabled")), Wait);
        dialog.Step("test-parent-activate").Click();
        await WaitForStateAsync(id, TestParentState.Active, cancellationToken);
        Assert.Equal("8", (await ValuesOfAsync(id, cancellationToken))[grade]);
    }

    [Fact]
    public async Task The_server_refuses_the_activation_of_a_stored_parent_without_a_required_custom_field_whatever_graph_it_is_given()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: true, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Number, required: true, cancellationToken);
        var without = await AddParentAsync(scenario.A, TestParentState.Draft, "Without", cancellationToken);
        var with = await AddParentWithValuesAsync(scenario.A, TestParentState.Draft, "With", cancellationToken, (grade, "7"));
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var workflows = host.Get<WorkflowService>();

        var refused = await workflows.TransitionEntityAsync<TestParentState, TestParent>(without, "test-parent-activate", cancellationToken: cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Grade and Room are required", refused.Error, StringComparison.Ordinal);

        var partly = await workflows.TransitionEntityAsync<TestParentState, TestParent>(with, "test-parent-activate", TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.Contains("Room is required", partly.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Grade", partly.Error, StringComparison.Ordinal);

        var theirs = await AddFieldAsync(scenario.B, "Their field", AttributeValueType.Text, required: true, cancellationToken);
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        using (writer.BeginSystemScope("test: fill the room as a save would"))
        {
            writer.TestParentAttributes.Add(new TestParentAttribute { EntityId = with, EntityAttributeDefinitionId = room, DoubleValue = 2 });
            await writer.SaveChangesAsync(cancellationToken);
        }

        var activated = await workflows.TransitionEntityAsync<TestParentState, TestParent>(with, "test-parent-activate", cancellationToken: cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);
        Assert.NotEqual(Guid.Empty, theirs);
    }

    [Fact]
    public async Task A_required_field_out_of_use_holds_no_step_back_keeps_its_values_and_takes_no_new_one()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var locker = await AddFieldAsync(scenario.A, "Locker", AttributeValueType.Text, required: true, cancellationToken);
        var kept = await AddParentWithValuesAsync(scenario.A, TestParentState.Draft, "Kept", cancellationToken, (locker, "L-1"));
        var without = await AddParentAsync(scenario.A, TestParentState.Draft, "Without", cancellationToken);
        var other = await AddParentAsync(scenario.A, TestParentState.Draft, "Other", cancellationToken);
        Guid desk;
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        using (writer.BeginSystemScope("test: a required draft field of the tenant's parents"))
        {
            var field = new TestParentAttributeDefinition { State = CustomFieldState.Draft, Name = "Desk", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, IsRequired = true };
            writer.TestParentAttributeDefinitions.Add(field);
            await writer.SaveChangesAsync(cancellationToken);
            desk = field.Id;
        }

        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = console.Get<WorkflowService>();
        Assert.True((await fields.TransitionEntityAsync<CustomFieldState, TestParentAttributeDefinition>(locker, CustomFieldWorkflowAttribute<TestParentAttributeDefinition>.DeactivateStep, cancellationToken: cancellationToken)).IsSuccess);

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var activated = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(without, "test-parent-activate", cancellationToken: cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error);

        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await writer.TestParents.SingleAsync(parent => parent.Id == kept, cancellationToken)).Name = "Kept, renamed";
            await writer.SaveChangesAsync(cancellationToken);
        }

        foreach (var write in new Func<TestDataContext, Task>[]
        {
            async context => (await context.TestParentAttributes.SingleAsync(value => value.EntityId == kept && value.EntityAttributeDefinitionId == locker, cancellationToken)).StringValue = "L-2",
            context =>
            {
                context.TestParentAttributes.Add(new TestParentAttribute { EntityId = other, EntityAttributeDefinitionId = locker, StringValue = "L-3" });
                return Task.CompletedTask;
            },
            context =>
            {
                context.TestParentAttributes.Add(new TestParentAttribute { EntityId = other, EntityAttributeDefinitionId = desk, StringValue = "D-1" });
                return Task.CompletedTask;
            },
        })
        {
            await using var writer = this.database.CreateContext(scenario.A.Runtime());
            await write(writer);
            var refused = await Assert.ThrowsAsync<CustomFieldException>(() => writer.SaveChangesAsync(cancellationToken));
            Assert.EndsWith("is not in use, so it takes no value.", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal("L-1", (await ValuesOfAsync(kept, cancellationToken))[locker]);
        Assert.Empty(await ValuesOfAsync(other, cancellationToken));

        Assert.True((await fields.TransitionEntityAsync<CustomFieldState, TestParentAttributeDefinition>(locker, CustomFieldWorkflowAttribute<TestParentAttributeDefinition>.ReactivateStep, cancellationToken: cancellationToken)).IsSuccess);
        var waiting = await host.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(other, "test-parent-activate", cancellationToken: cancellationToken);
        Assert.Contains("Locker is required", waiting.Error, StringComparison.Ordinal);
        Assert.Equal("L-1", (await ValuesOfAsync(kept, cancellationToken))[locker]);
    }

    [Fact]
    public async Task A_changed_custom_field_of_an_active_parent_is_refused_through_its_updater_while_an_unchanged_one_passes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: false, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken, (grade, "7"));
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = host.Get<TestParentDialogService>();

        foreach (var edit in new Action<TestParent>[]
        {
            parent => parent.Attributes.Single().StringValue = "8",
            parent => parent.Attributes.Add(new TestParentAttribute { EntityAttributeDefinitionId = room, StringValue = "North" }),
            parent => parent.Attributes.Clear(),
        })
        {
            var updater = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
            edit(updater.Entity);
            var saved = await service.UpdateTestParentAsync(updater, cancellationToken);
            Assert.False(saved.IsSuccess);
            Assert.Equal("Custom fields cannot be changed while the TestParent is Active.", saved.Error);
        }

        var notes = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        notes.Entity.Notes = "Still editable";
        var passed = await service.UpdateTestParentAsync(notes, cancellationToken);
        Assert.True(passed.IsSuccess, passed.Error);
        Assert.Equal("Still editable", (await LoadAsync(id, cancellationToken))!.Notes);
        Assert.Equal(new Dictionary<Guid, string?> { [grade] = "7" }, await ValuesOfAsync(id, cancellationToken));
    }

    [Fact]
    public async Task A_changed_custom_field_sent_with_a_step_is_refused_and_rolls_the_step_back()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, Staffora.Core.Entities.Tenancy.RolePermission.SettingsEdit);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken, (grade, "7"));
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime(), services => services.AddScoped<TestParentDialogService>());
        var service = host.Get<TestParentDialogService>();

        var changed = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        changed.Entity.Attributes.Single().StringValue = "8";
        var refused = await service.UpdateTestParentAndTransitionAsync(changed, "test-parent-cancel", "A reason", cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Custom fields cannot be changed while the TestParent is Active.", refused.Error, StringComparison.Ordinal);
        Assert.Equal(TestParentState.Active, (await LoadAsync(id, cancellationToken))!.State);
        Assert.Equal("7", (await ValuesOfAsync(id, cancellationToken))[grade]);
        Assert.True(changed.HasChanges());

        var unchanged = (await service.LoadTestParentUpdaterAsync(id, cancellationToken)).Value!;
        var ran = await service.UpdateTestParentAndTransitionAsync(unchanged, "test-parent-cancel", "A reason", cancellationToken);
        Assert.True(ran.IsSuccess, ran.Error);
        Assert.Equal(TestParentState.Canceled, (await LoadAsync(id, cancellationToken))!.State);
    }

    [Fact]
    public async Task A_custom_field_written_past_the_updater_is_judged_by_the_persisted_state_and_an_unchanged_one_passes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", AttributeValueType.Text, required: false, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", AttributeValueType.Text, required: false, cancellationToken);
        var id = await AddParentWithValuesAsync(scenario.A, TestParentState.Active, "Active parent", cancellationToken, (grade, "7"));

        foreach (var write in new Func<TestDataContext, Task>[]
        {
            async context => (await context.TestParentAttributes.SingleAsync(value => value.EntityId == id, cancellationToken)).StringValue = "8",
            context => Task.FromResult(context.TestParentAttributes.Add(new TestParentAttribute { EntityId = id, EntityAttributeDefinitionId = room, StringValue = "North" })),
            async context => context.TestParentAttributes.Remove(await context.TestParentAttributes.SingleAsync(value => value.EntityId == id, cancellationToken)),
        })
        {
            await using var context = this.database.CreateContext(scenario.A.Runtime());
            await write(context);
            var refusal = await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Contains("its Custom fields cannot be edited while it is Active", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(["Attributes"], refusal.Fields);
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            var stored = await context.TestParentAttributes.AsNoTracking().SingleAsync(value => value.EntityId == id, cancellationToken);
            context.TestParentAttributes.Update(stored);
            await context.SaveChangesAsync(cancellationToken);
        }

        var draft = await AddParentWithValuesAsync(scenario.A, TestParentState.Draft, "Draft parent", cancellationToken, (grade, "1"));
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            (await context.TestParentAttributes.SingleAsync(value => value.EntityId == draft, cancellationToken)).StringValue = "2";
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal("7", (await ValuesOfAsync(id, cancellationToken))[grade]);
        Assert.Equal("2", (await ValuesOfAsync(draft, cancellationToken))[grade]);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        using (context.BeginSystemScope("test: delete the field with its values"))
        {
            context.TestParentAttributes.RemoveRange(await context.TestParentAttributes.Where(value => value.EntityAttributeDefinitionId == grade).ToListAsync(cancellationToken));
            context.TestParentAttributeDefinitions.Remove(await context.TestParentAttributeDefinitions.SingleAsync(field => field.Id == grade, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal("7", (await ValuesOfAsync(id, cancellationToken))[grade]);
    }

    [Fact]
    public async Task A_record_created_with_custom_field_values_is_judged_by_the_state_it_is_inserted_in()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        Guid field;
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        {
            using var lease = writer.BeginSystemScope("test: a field of A's folders");
            var definition = new TestFolderAttributeDefinition { State = CustomFieldState.Active, Name = "Shelf", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId };
            writer.TestFolderAttributeDefinitions.Add(definition);
            await writer.SaveChangesAsync(cancellationToken);
            field = definition.Id;
        }

        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var workflows = host.Get<WorkflowService>();

        var withValue = new TestFolder { Name = "With a shelf", Attributes = [new TestFolderAttribute { EntityAttributeDefinitionId = field, StringValue = "A" }] };
        var refused = await workflows.CreateAndTransitionAsync<TestFolderState, TestFolder>(withValue, "test-folder-seal-the-documents", cancellationToken: cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Contains("its Custom fields cannot be edited while it is Draft", refused.Error, StringComparison.Ordinal);

        var without = await workflows.CreateAndTransitionAsync<TestFolderState, TestFolder>(new TestFolder { Name = "Without" }, "test-folder-seal-the-documents", cancellationToken: cancellationToken);
        Assert.True(without.IsSuccess, without.Error);

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.TestFolders.Add(new TestFolder { Name = "Inserted", Attributes = [new TestFolderAttribute { EntityAttributeDefinitionId = field, StringValue = "B" }] });
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using (var seeder = this.database.CreateContext(scenario.A.Runtime()))
        using (seeder.BeginSystemScope("test: seed a folder with its value"))
        {
            seeder.TestFolders.Add(new TestFolder { Name = "Seeded", Attributes = [new TestFolderAttribute { EntityAttributeDefinitionId = field, StringValue = "C" }] });
            await seeder.SaveChangesAsync(cancellationToken);
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal(["Seeded", "Without"], await system.TestFolders.Where(folder => folder.TenantId == scenario.A.TenantId).OrderBy(folder => folder.Name).Select(folder => folder.Name).ToListAsync(cancellationToken));
        Assert.Equal(["C"], await system.TestFolderAttributes.Select(value => value.StringValue).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Choosing_a_parent_in_the_drop_down_sets_the_reference_too_so_the_activation_is_judged_in_memory()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var first = await AddParentAsync(scenario.A, TestParentState.Draft, "First parent", cancellationToken);
        var closed = await AddParentAsync(scenario.A, TestParentState.Draft, "Closed parent", cancellationToken);
        await UpdateParentAsync(closed, parent => parent.Notes = TestParent.TakesNoActiveChildren, cancellationToken);
        var child = await AddChildAsync(scenario.A, first, "Child", cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime());

        var dialog = await circuit.OpenChildLoadedAsync(child);
        Assert.Contains("test-child-activate", dialog.StepKeys());
        var asked = dialog.AnyContext<TestChildState, TestChild>().OfferRequests;

        Option(dialog, "Closed parent").Click();
        dialog.WaitForEdit();
        var context = dialog.AnyContext<TestChildState, TestChild>();
        Assert.Equal(closed, context.Entity!.ParentId);
        Assert.Equal(closed, context.Entity.Parent!.Id);
        dialog.Host.WaitForAssertion(() => Assert.DoesNotContain("test-child-activate", dialog.StepKeys()), Wait);
        Assert.Equal(asked, context.OfferRequests);

        var refused = await circuit.ChildService.UpdateTestChildAndTransitionAsync(context.Updater!, "test-child-activate", null, cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Contains("The parent takes active children", refused.Error, StringComparison.Ordinal);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Equal(first, (await system.TestChildren.AsNoTracking().SingleAsync(row => row.Id == child, cancellationToken)).ParentId);
        }

        Option(dialog, "First parent").Click();
        dialog.Host.WaitForAssertion(() => Assert.Contains("test-child-activate", dialog.StepKeys()), Wait);
        Assert.Equal(first, context.Entity.Parent!.Id);
        Assert.Equal(asked, context.OfferRequests);
    }

    private static IElement Field(OpenDialog dialog, string field) =>
        dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelector($".state-field[data-field={field}]") ?? throw new Xunit.Sdk.XunitException($"No field {field} is drawn.");

    private static IElement Input(OpenDialog dialog, string name) =>
        dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelector($"[name='{name}']") ?? throw new Xunit.Sdk.XunitException($"No input {name} is drawn.");

    private static IElement CustomField(OpenDialog dialog, Guid field) =>
        dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelector($"[data-attribute-field='{field}']") ?? throw new Xunit.Sdk.XunitException($"No custom field {field} is drawn.");

    private static string Card(OpenDialog dialog, IElement step) => dialog.Host.Find($"#{step.GetAttribute("aria-describedby")}").TextContent;


    private static IElement Option(OpenDialog dialog, string text) =>
        dialog.Host.FindAll(".state-entity-dialog").Last().QuerySelectorAll("li.rz-dropdown-item").Single(option => option.GetAttribute("aria-label") == text);

    private async Task<Guid> AddFieldAsync(ScenarioTenant tenant, string name, AttributeValueType type, bool required, CancellationToken cancellationToken, params string[] options)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: a field of the tenant's parents");
        var field = new TestParentAttributeDefinition
        {
            State = CustomFieldState.Active,
            Name = name,
            ValueType = type,
            TenantId = tenant.TenantId,
            IsRequired = required,
            Options = type == AttributeValueType.Choice ? AttributeChoiceOption.WithKeys([.. options.Select(label => new AttributeChoiceOption(string.Empty, label))], []) : [],
        };
        writer.TestParentAttributeDefinitions.Add(field);
        await writer.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task<Guid> AddParentWithValuesAsync(ScenarioTenant tenant, TestParentState state, string name, CancellationToken cancellationToken, params (Guid Field, string Text)[] values)
    {
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: insert a parent with its values as a transition left it");
        var parent = new TestParent { Name = name, State = state };
        foreach (var (field, text) in values)
        {
            parent.Attributes.Add(new TestParentAttribute { EntityAttributeDefinitionId = field, StringValue = text });
        }

        writer.TestParents.Add(parent);
        await writer.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<Dictionary<Guid, string?>> ValuesOfAsync(Guid parent, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParentAttributes.AsNoTracking().Where(value => value.EntityId == parent).ToDictionaryAsync(value => value.EntityAttributeDefinitionId, value => value.StringValue, cancellationToken);
    }

    private async Task MoveChildAsync(TwoTenantScenario scenario, Guid id, string transitionKey, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TestChildState, TestChild>(id, transitionKey, TestChildWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        Assert.True(moved.IsSuccess, moved.Error);
    }
}
