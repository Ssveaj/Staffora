using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Staffora.Blazor.Components.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;

namespace Staffora.Test.Web.EntityAttributes;

public class AttributeValueFieldTests : WebTestContext
{
    [Fact]
    public async Task A_text_field_is_a_named_text_box_writing_the_text_column()
    {
        var (field, value, changes) = Render(AttributeValueType.Text);

        var input = field.Find($"input[name='user-attribute-{value.Definition.Id}']");
        Assert.Equal("Notes", field.Find($"label[for='user-attribute-{value.Definition.Id}']").TextContent.Trim());
        Assert.Equal(AttributeValueRules.TextMaxLength.ToString(), input.GetAttribute("maxlength"));
        input.Change("Left-handed");

        Assert.Equal("Left-handed", value.Row.StringValue);
        Assert.True(OnlyColumn(value.Row, nameof(IEntityAttributeValue.StringValue)));
        Assert.Equal(1, changes());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_whole_number_and_a_number_are_named_numeric_boxes_writing_their_own_columns()
    {
        var (whole, wholeValue, wholeChanges) = Render(AttributeValueType.Integer);
        Assert.Equal("Notes", whole.Find($"label[for='user-attribute-{wholeValue.Definition.Id}']").TextContent.Trim());
        var wholeBox = whole.FindComponent<RadzenNumeric<int?>>();
        Assert.Equal($"user-attribute-{wholeValue.Definition.Id}", wholeBox.Instance.Name);
        await whole.InvokeAsync(() => wholeBox.Instance.ValueChanged.InvokeAsync(12));
        Assert.Equal(12, wholeValue.Row.Int32Value);
        Assert.True(OnlyColumn(wholeValue.Row, nameof(IEntityAttributeValue.Int32Value)));
        Assert.Equal(1, wholeChanges());

        var (number, numberValue, _) = Render(AttributeValueType.Number);
        var numberBox = number.FindComponent<RadzenNumeric<double?>>();
        Assert.Equal($"user-attribute-{numberValue.Definition.Id}", numberBox.Instance.Name);
        await number.InvokeAsync(() => numberBox.Instance.ValueChanged.InvokeAsync(2.5));
        Assert.Equal(2.5, numberValue.Row.DoubleValue);
        Assert.True(OnlyColumn(numberValue.Row, nameof(IEntityAttributeValue.DoubleValue)));
    }

    [Fact]
    public async Task A_date_is_a_named_date_picker_over_a_calendar_day()
    {
        var (field, value, changes) = Render(AttributeValueType.Date);

        var picker = field.FindComponent<RadzenDatePicker<DateOnly?>>();
        Assert.Equal($"user-attribute-{value.Definition.Id}", picker.Instance.Name);
        Assert.Equal("Notes", field.Find($"label[for='user-attribute-{value.Definition.Id}']").TextContent.Trim());
        await field.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(new DateOnly(2026, 11, 3)));

        Assert.Equal(new DateOnly(2026, 11, 3), value.Row.DateValue);
        Assert.True(OnlyColumn(value.Row, nameof(IEntityAttributeValue.DateValue)));
        Assert.Equal(1, changes());
    }

    [Fact]
    public async Task A_yes_or_no_is_a_named_check_box_beside_its_label()
    {
        var (field, value, changes) = Render(AttributeValueType.Boolean);

        var box = field.FindComponent<RadzenCheckBox<bool>>();
        Assert.Equal($"user-attribute-{value.Definition.Id}", box.Instance.Name);
        Assert.Equal("Notes", field.Find("[role=checkbox]").GetAttribute("aria-label"));
        Assert.Equal("Notes", field.Find($"label[for='user-attribute-{value.Definition.Id}']").TextContent.Trim());
        await field.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(true));

        Assert.True(value.Row.BooleanValue);
        Assert.True(OnlyColumn(value.Row, nameof(IEntityAttributeValue.BooleanValue)));
        Assert.Equal(1, changes());
    }

    [Fact]
    public async Task A_choice_offers_its_options_by_label_and_stores_the_key()
    {
        var (field, value, changes) = Render(AttributeValueType.Choice);

        var dropDown = field.FindComponent<RadzenDropDown<string>>();
        Assert.Equal($"user-attribute-{value.Definition.Id}", dropDown.Instance.Name);
        Assert.Equal("Team", field.Find($"label[for='user-attribute-{value.Definition.Id}']").TextContent.Trim());
        Assert.Equal("Team", dropDown.Instance.InputAttributes!["aria-label"]);

        Assert.Equal([("teaching", "Working"), ("planning", "Planning")], Offered(dropDown));

        await field.InvokeAsync(() => dropDown.Instance.ValueChanged.InvokeAsync("planning"));
        Assert.Equal("planning", value.Row.StringValue);
        Assert.True(OnlyColumn(value.Row, nameof(IEntityAttributeValue.StringValue)));

        await field.InvokeAsync(() => dropDown.Instance.ValueChanged.InvokeAsync(null));
        Assert.Null(value.Row.StringValue);
        Assert.Equal(2, changes());
    }

    [Fact]
    public async Task A_choice_holding_a_retired_or_unknown_option_shows_it_marked()
    {
        var (retired, _, _) = Render(AttributeValueType.Choice, row => row.StringValue = "support");
        Assert.Equal([("teaching", "Working"), ("planning", "Planning"), ("support", "Support (retired)")], Offered(retired.FindComponent<RadzenDropDown<string>>()));

        var (unknown, _, _) = Render(AttributeValueType.Choice, row => row.StringValue = "gone");
        Assert.Equal([("teaching", "Working"), ("planning", "Planning"), ("gone", "(unknown option)")], Offered(unknown.FindComponent<RadzenDropDown<string>>()));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(AttributeValueType.Text)]
    [InlineData(AttributeValueType.Integer)]
    [InlineData(AttributeValueType.Number)]
    [InlineData(AttributeValueType.Date)]
    [InlineData(AttributeValueType.Boolean)]
    [InlineData(AttributeValueType.Choice)]
    public async Task A_disabled_field_shows_its_input_disabled(AttributeValueType type)
    {
        var (field, value, _) = Render(type, disabled: true);

        var disabled = field.FindComponents<RadzenTextBox>().Select(input => input.Instance.Disabled)
            .Concat(field.FindComponents<RadzenNumeric<int?>>().Select(input => input.Instance.Disabled))
            .Concat(field.FindComponents<RadzenNumeric<double?>>().Select(input => input.Instance.Disabled))
            .Concat(field.FindComponents<RadzenDatePicker<DateOnly?>>().Select(input => input.Instance.Disabled))
            .Concat(field.FindComponents<RadzenCheckBox<bool>>().Select(input => input.Instance.Disabled))
            .Concat(field.FindComponents<RadzenDropDown<string>>().Select(input => input.Instance.Disabled))
            .ToList();
        Assert.NotEmpty(disabled);
        Assert.All(disabled, Assert.True);
        Assert.True(AttributeValues.IsEmpty(value.Row));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_type_that_takes_no_values_renders_no_input()
    {
        var (field, _, _) = Render((AttributeValueType)5);

        Assert.Empty(field.FindAll("input, textarea, select, [role=checkbox], [role=combobox]"));
        Assert.Contains("this field's type takes no values", field.Markup, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Each_type_renders_exactly_one_input_of_its_kind_and_no_other()
    {
        var kinds = new Dictionary<AttributeValueType, Type>
        {
            [AttributeValueType.Text] = typeof(RadzenTextBox),
            [AttributeValueType.Integer] = typeof(RadzenNumeric<int?>),
            [AttributeValueType.Number] = typeof(RadzenNumeric<double?>),
            [AttributeValueType.Date] = typeof(RadzenDatePicker<DateOnly?>),
            [AttributeValueType.Boolean] = typeof(RadzenCheckBox<bool>),
            [AttributeValueType.Choice] = typeof(RadzenDropDown<string>),
        };

        Assert.Equal(AttributeDefinitionRules.Types.Order(), kinds.Keys.Order());
        foreach (var (type, kind) in kinds)
        {
            var (field, _, _) = Render(type);
            var inputs = field.FindComponents<IComponent>().Select(component => component.Instance.GetType()).Where(kinds.ContainsValue).ToList();
            Assert.Equal([kind], inputs);
            Assert.Equal(type.ToString().ToLowerInvariant(), field.Find(".attribute-value-field").GetAttribute("data-value-type"));
        }

        await Task.CompletedTask;
    }

    private (IRenderedComponent<AttributeValueField> Field, (ApplicationUserAttributeDefinition Definition, ApplicationUserAttribute Row) Value, Func<int> Changes) Render(
        AttributeValueType type, Action<ApplicationUserAttribute>? arrange = null, bool disabled = false)
    {
        var definition = new ApplicationUserAttributeDefinition
        {
            State = CustomFieldState.Active,
            Id = Guid.NewGuid(),
            Name = type == AttributeValueType.Choice ? "Team" : "Notes",
            ValueType = type,
            Options = type == AttributeValueType.Choice ? [new("teaching", "Working"), new("planning", "Planning"), new("support", "Support", IsRetired: true)] : [],
        };
        var row = new ApplicationUserAttribute { EntityAttributeDefinitionId = definition.Id };
        arrange?.Invoke(row);
        var changes = 0;
        var field = Render<AttributeValueField>(parameters => parameters
            .Add(component => component.Definition, definition)
            .Add(component => component.Value, row)
            .Add(component => component.NamePrefix, "user-attribute")
            .Add(component => component.Disabled, disabled)
            .Add(component => component.Changed, () => changes++));
        return (field, (definition, row), () => changes);
    }

    private static List<(string Key, string Text)> Offered(IRenderedComponent<RadzenDropDown<string>> dropDown) =>
        [.. dropDown.Instance.Data!.Cast<object>().Select(item => ((string)item.GetType().GetProperty("Key")!.GetValue(item)!, (string)item.GetType().GetProperty("Text")!.GetValue(item)!))];

    private static bool OnlyColumn(IEntityAttributeValue row, string column) =>
        (row.StringValue is not null) == (column == nameof(IEntityAttributeValue.StringValue))
        && (row.Int32Value is not null) == (column == nameof(IEntityAttributeValue.Int32Value))
        && (row.DoubleValue is not null) == (column == nameof(IEntityAttributeValue.DoubleValue))
        && (row.BooleanValue is not null) == (column == nameof(IEntityAttributeValue.BooleanValue))
        && (row.DateValue is not null) == (column == nameof(IEntityAttributeValue.DateValue));
}
